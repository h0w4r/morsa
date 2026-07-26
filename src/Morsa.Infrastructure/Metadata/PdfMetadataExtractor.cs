// SPDX-License-Identifier: GPL-3.0-or-later
// Selective clean port of FOCA PDFDocument.cs and XMPExtractor.cs at commit 754453ad7f9579a6021c484d5014a3cd12fd0e35.
using System.IO.Compression;
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>
/// Extracts PDF Info, XMP/RDF, decoded metadata streams and embedded-image metadata under strict
/// byte/count budgets. The parser does not render pages, execute actions or resolve external data.
/// </summary>
public sealed class PdfMetadataExtractor : IArtifactExtractor
{
    private const long MaximumParserWindow = 64L * 1024 * 1024;
    private const long MaximumSingleDecodedStream = 32L * 1024 * 1024;
    private static readonly (string Name, string Category)[] InfoProperties =
    [
        ("Author", "author"),
        ("Creator", "application"),
        ("Producer", "application"),
        ("CreationDate", "date"),
        ("ModDate", "date"),
        ("Subject", "subject"),
        ("Title", "title"),
        ("Keywords", "keywords"),
        ("Trapped", "pdf.trapped"),
    ];

    public string Id => "builtin.pdf";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Pdf];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var diagnostics = new List<ExtractionDiagnostic>();
        var maximum = Math.Min(options.MaxBytes, MaximumParserWindow);
        var info = new FileInfo(artifact.Path);
        if (info.Length > maximum)
        {
            return new ExtractionResult([], [], [new("pdf.size_budget", "PDF exceeds the bounded parser window.", true)]);
        }

        var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            return new ExtractionResult([], [], [new("pdf.invalid_signature", "PDF signature is missing.", true)]);
        }

        ExtractInfoDictionaryValues(bytes, artifact.ArtifactId, observations);
        ExtractFileSpecificationValues(bytes, artifact.ArtifactId, observations);
        StructuredMetadataUtilities.AddXmpPackets(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "pdf/raw",
            observations,
            diagnostics);
        StructuredMetadataUtilities.AddLooseXmpProperties(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "pdf/raw",
            observations);
        StructuredMetadataUtilities.AddBinaryIndicators(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "pdf/raw",
            observations);

        ReadStreams(bytes, artifact, options, observations, diagnostics, cancellationToken);

        return new ExtractionResult(
            observations.DistinctBy(item => (item.Category, item.NormalizedValue, item.Location)).ToArray(),
            [],
            diagnostics);
    }

    private static void ExtractInfoDictionaryValues(
        byte[] bytes,
        Guid artifactId,
        ICollection<MetadataObservation> observations)
    {
        foreach (var (name, category) in InfoProperties)
        {
            var token = Encoding.ASCII.GetBytes($"/{name}");
            var cursor = 0;
            var occurrence = 0;
            while (cursor <= bytes.Length - token.Length && occurrence < 1_000)
            {
                var relative = bytes.AsSpan(cursor).IndexOf(token);
                if (relative < 0)
                {
                    break;
                }

                var index = cursor + relative + token.Length;
                cursor = index;
                if (index < bytes.Length && IsPdfNameCharacter(bytes[index]))
                {
                    continue;
                }

                SkipWhiteSpaceAndComments(bytes, ref index);
                if (TryReadPdfString(bytes, ref index, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        category,
                        value,
                        "builtin.pdf",
                        "1.0.0",
                        occurrence == 0 ? $"pdf/info/{name}" : $"pdf/info/{name}:{occurrence}"));
                }

                occurrence++;
            }
        }
    }

    private static void ExtractFileSpecificationValues(
        byte[] bytes,
        Guid artifactId,
        ICollection<MetadataObservation> observations)
    {
        foreach (var name in new[] { "UF", "F", "Desc" })
        {
            var token = Encoding.ASCII.GetBytes($"/{name}");
            var cursor = 0;
            var occurrence = 0;
            while (cursor <= bytes.Length - token.Length && occurrence < 1_000)
            {
                var relative = bytes.AsSpan(cursor).IndexOf(token);
                if (relative < 0)
                {
                    break;
                }

                var index = cursor + relative + token.Length;
                cursor = index;
                SkipWhiteSpaceAndComments(bytes, ref index);
                if (TryReadPdfString(bytes, ref index, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        name == "Desc" ? "comments" : "embedded_object",
                        value,
                        "builtin.pdf",
                        "1.0.0",
                        $"pdf/filespec/{name}:{occurrence}",
                        0.85));
                }

                occurrence++;
            }
        }
    }

    private static void ReadStreams(
        byte[] bytes,
        ArtifactContext artifact,
        ExtractionOptions options,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var cursor = 0;
        var streamIndex = 0;
        long totalDecoded = 0;
        while (cursor < text.Length && streamIndex < options.MaxContainerEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streamToken = text.IndexOf("stream", cursor, StringComparison.Ordinal);
            if (streamToken < 0)
            {
                break;
            }

            var dictionaryEnd = text.LastIndexOf(">>", streamToken, StringComparison.Ordinal);
            var dictionaryStart = dictionaryEnd < 0
                ? -1
                : text.LastIndexOf("<<", dictionaryEnd, StringComparison.Ordinal);
            if (dictionaryStart < 0 || dictionaryEnd < dictionaryStart || streamToken - (dictionaryEnd + 2) > 8)
            {
                cursor = streamToken + "stream".Length;
                continue;
            }

            var dataStart = streamToken + "stream".Length;
            if (dataStart < text.Length && text[dataStart] == '\r') dataStart++;
            if (dataStart < text.Length && text[dataStart] == '\n') dataStart++;
            var endStream = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (endStream < 0)
            {
                diagnostics.Add(new("pdf.stream_truncated", $"PDF stream {streamIndex} is incomplete.", false));
                break;
            }

            var dataEnd = endStream;
            while (dataEnd > dataStart && text[dataEnd - 1] is '\r' or '\n') dataEnd--;
            var dictionary = text.Substring(dictionaryStart, dictionaryEnd + 2 - dictionaryStart);
            var encoded = bytes.AsMemory(dataStart, dataEnd - dataStart);
            ReadOneStream(
                dictionary,
                encoded,
                streamIndex,
                artifact,
                options,
                ref totalDecoded,
                observations,
                diagnostics);

            streamIndex++;
            cursor = endStream + "endstream".Length;
        }

        if (streamIndex == options.MaxContainerEntries && cursor < text.Length)
        {
            diagnostics.Add(new("pdf.stream_count_budget", "PDF stream count budget reached.", true));
        }
    }

    private static void ReadOneStream(
        string dictionary,
        ReadOnlyMemory<byte> encoded,
        int streamIndex,
        ArtifactContext artifact,
        ExtractionOptions options,
        ref long totalDecoded,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        ReadOnlyMemory<byte> decoded = encoded;
        if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
        {
            var remaining = Math.Max(0, options.MaxUncompressedBytes - totalDecoded);
            var maximum = Math.Min(remaining, MaximumSingleDecodedStream);
            if (maximum == 0)
            {
                diagnostics.Add(new("pdf.decoded_budget", "PDF decoded stream budget reached.", true));
                return;
            }

            try
            {
                decoded = InflateBounded(encoded, maximum);
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new("pdf.flate_invalid", $"PDF stream {streamIndex}: {exception.Message}", false));
                return;
            }
        }

        totalDecoded = checked(totalDecoded + decoded.Length);
        if (totalDecoded > options.MaxUncompressedBytes)
        {
            diagnostics.Add(new("pdf.decoded_budget", "PDF aggregate decoded stream budget reached.", true));
            return;
        }

        var location = $"pdf/stream:{streamIndex}";
        var isMetadata = dictionary.Contains("/Type /Metadata", StringComparison.Ordinal) ||
                         decoded.Span.IndexOf("<x:xmpmeta"u8) >= 0 ||
                         decoded.Span.IndexOf("<rdf:RDF"u8) >= 0;
        if (isMetadata)
        {
            StructuredMetadataUtilities.AddXmpPackets(
                decoded.Span,
                artifact.ArtifactId,
                "builtin.pdf",
                "1.0.0",
                location,
                observations,
                diagnostics);
            StructuredMetadataUtilities.AddLooseXmpProperties(
                decoded.Span,
                artifact.ArtifactId,
                "builtin.pdf",
                "1.0.0",
                location,
                observations);
        }

        var isImage = dictionary.Contains("/Subtype /Image", StringComparison.Ordinal);
        var isJpeg = dictionary.Contains("/DCTDecode", StringComparison.Ordinal) ||
                     decoded.Span.StartsWith(new byte[] { 0xff, 0xd8, 0xff });
        if (isImage && isJpeg)
        {
            StructuredMetadataUtilities.AddEmbeddedImageMetadata(
                decoded,
                artifact.ArtifactId,
                "builtin.pdf",
                "1.0.0",
                $"{location}/image",
                observations,
                diagnostics);
        }

        if (dictionary.Contains("/Type /EmbeddedFile", StringComparison.Ordinal))
        {
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId,
                "embedded_object",
                ReadPdfName(dictionary, "/Subtype") ?? $"embedded-file:{streamIndex}",
                "builtin.pdf",
                "1.0.0",
                location,
                1.0));
            StructuredMetadataUtilities.FindAndAddEmbeddedImages(
                decoded,
                artifact.ArtifactId,
                "builtin.pdf",
                "1.0.0",
                $"{location}/embedded-file",
                observations,
                diagnostics);
        }

        // Decoded content may contain links or local paths hidden by Flate compression.
        StructuredMetadataUtilities.AddBinaryIndicators(
            decoded.Span,
            artifact.ArtifactId,
            "builtin.pdf",
            "1.0.0",
            location,
            observations);
    }

    private static ReadOnlyMemory<byte> InflateBounded(ReadOnlyMemory<byte> encoded, long maximum)
    {
        using var input = new MemoryStream(encoded.ToArray(), writable: false);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = inflater.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximum)
            {
                throw new InvalidDataException("Decoded PDF stream exceeds its byte budget.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static bool TryReadPdfString(byte[] bytes, ref int index, out string value)
    {
        value = string.Empty;
        if (index >= bytes.Length)
        {
            return false;
        }

        if (bytes[index] == (byte)'(')
        {
            return TryReadLiteralString(bytes, ref index, out value);
        }

        if (bytes[index] == (byte)'<' && (index + 1 >= bytes.Length || bytes[index + 1] != (byte)'<'))
        {
            return TryReadHexString(bytes, ref index, out value);
        }

        return false;
    }

    private static bool TryReadLiteralString(byte[] bytes, ref int index, out string value)
    {
        var decoded = new List<byte>(256);
        var depth = 1;
        index++;
        while (index < bytes.Length && decoded.Count <= 16_384)
        {
            var current = bytes[index++];
            if (current == (byte)'\\')
            {
                if (index >= bytes.Length) break;
                var escaped = bytes[index++];
                if (escaped == (byte)'\r')
                {
                    if (index < bytes.Length && bytes[index] == (byte)'\n') index++;
                    continue;
                }

                if (escaped == (byte)'\n') continue;
                if (escaped is >= (byte)'0' and <= (byte)'7')
                {
                    var octal = escaped - (byte)'0';
                    for (var count = 1; count < 3 && index < bytes.Length && bytes[index] is >= (byte)'0' and <= (byte)'7'; count++)
                    {
                        octal = (octal * 8) + bytes[index++] - (byte)'0';
                    }

                    decoded.Add((byte)octal);
                    continue;
                }

                decoded.Add(escaped switch
                {
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'b' => (byte)'\b',
                    (byte)'f' => (byte)'\f',
                    _ => escaped,
                });
                continue;
            }

            if (current == (byte)'(')
            {
                depth++;
                decoded.Add(current);
                continue;
            }

            if (current == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    value = DecodePdfString(CollectionsMarshalAsSpan(decoded));
                    return true;
                }

                decoded.Add(current);
                continue;
            }

            decoded.Add(current);
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadHexString(byte[] bytes, ref int index, out string value)
    {
        var hexadecimal = new List<byte>(256);
        index++;
        while (index < bytes.Length && hexadecimal.Count <= 32_768)
        {
            var current = bytes[index++];
            if (current == (byte)'>')
            {
                if ((hexadecimal.Count & 1) != 0) hexadecimal.Add((byte)'0');
                var decoded = new byte[hexadecimal.Count / 2];
                for (var offset = 0; offset < hexadecimal.Count; offset += 2)
                {
                    decoded[offset / 2] = (byte)((HexValue(hexadecimal[offset]) << 4) | HexValue(hexadecimal[offset + 1]));
                }

                value = DecodePdfString(decoded);
                return true;
            }

            if (IsWhiteSpace(current)) continue;
            if (!IsHexadecimal(current))
            {
                value = string.Empty;
                return false;
            }

            hexadecimal.Add(current);
        }

        value = string.Empty;
        return false;
    }

    private static string DecodePdfString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            var characterCount = (bytes.Length - 2) / 2;
            var characters = new char[characterCount];
            for (var index = 0; index < characterCount; index++)
            {
                characters[index] = (char)((bytes[2 + (index * 2)] << 8) | bytes[3 + (index * 2)]);
            }

            return new string(characters).TrimEnd('\0');
        }

        if (bytes.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return Encoding.Unicode.GetString(bytes[2..]).TrimEnd('\0');
        }

        return Encoding.Latin1.GetString(bytes).TrimEnd('\0');
    }

    private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> value) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(value);

    private static void SkipWhiteSpaceAndComments(byte[] bytes, ref int index)
    {
        while (index < bytes.Length)
        {
            while (index < bytes.Length && IsWhiteSpace(bytes[index])) index++;
            if (index >= bytes.Length || bytes[index] != (byte)'%') return;
            while (index < bytes.Length && bytes[index] is not (byte)'\r' and not (byte)'\n') index++;
        }
    }

    private static string? ReadPdfName(string dictionary, string property)
    {
        var index = dictionary.IndexOf(property, StringComparison.Ordinal);
        if (index < 0) return null;
        index += property.Length;
        while (index < dictionary.Length && char.IsWhiteSpace(dictionary[index])) index++;
        if (index >= dictionary.Length || dictionary[index] != '/') return null;
        var start = ++index;
        while (index < dictionary.Length && !char.IsWhiteSpace(dictionary[index]) && dictionary[index] is not '/' and not '>' and not '<') index++;
        return index > start ? dictionary[start..index] : null;
    }

    private static bool IsPdfNameCharacter(byte value) =>
        !IsWhiteSpace(value) && value is not (byte)'/' and not (byte)'<' and not (byte)'>' and not (byte)'[' and not (byte)']' and not (byte)'(' and not (byte)')';

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsHexadecimal(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => value - (byte)'a' + 10,
    };
}
