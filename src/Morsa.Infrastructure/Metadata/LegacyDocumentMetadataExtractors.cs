// SPDX-License-Identifier: GPL-3.0-or-later
// Selective clean port of FOCA InDDDocument.cs and WPDDocument.cs at commit 754453ad7f9579a6021c484d5014a3cd12fd0e35.
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>
/// Extracts bounded path, printer and XMP metadata from InDesign documents without loading the
/// document model. This preserves the useful behavior of FOCA's InDDDocument while removing its
/// unbounded regular expressions and XML parser surface.
/// </summary>
public sealed class InDesignMetadataExtractor : IArtifactExtractor
{
    private const long MaximumParserWindow = 64L * 1024 * 1024;

    public string Id => "builtin.indesign";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.InDesign];

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
            return new ExtractionResult([], [], [new("indesign.size_budget", "InDesign file exceeds the parser window.", true)]);
        }

        var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        StructuredMetadataUtilities.AddBinaryIndicators(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "indesign/binary",
            observations);
        StructuredMetadataUtilities.AddXmpPackets(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "indesign",
            observations,
            diagnostics);
        StructuredMetadataUtilities.FindAndAddEmbeddedImages(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "indesign",
            observations,
            diagnostics);

        return new ExtractionResult(
            observations.DistinctBy(item => (item.Category, item.NormalizedValue, item.Location)).ToArray(),
            [],
            diagnostics);
    }
}

/// <summary>
/// Reads WordPerfect's bounded UTF-16 metadata records and supplements them with safe printable
/// string scanning. The parser validates the FF-WPC signature before interpreting record markers.
/// </summary>
public sealed class WordPerfectMetadataExtractor : IArtifactExtractor
{
    private static readonly byte[] WordPerfectSignature = [0xff, 0x57, 0x50, 0x43];
    private const long MaximumParserWindow = 64L * 1024 * 1024;
    private const int MaximumRecordCharacters = 4_096;
    private const int MaximumRecords = 10_000;

    public string Id => "builtin.wordperfect";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.WordPerfect];

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
            return new ExtractionResult([], [], [new("wordperfect.size_budget", "WordPerfect file exceeds the parser window.", true)]);
        }

        var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        if (!bytes.AsSpan().StartsWith(WordPerfectSignature))
        {
            return new ExtractionResult([], [], [new("wordperfect.invalid_signature", "WordPerfect FF-WPC signature is missing.", true)]);
        }

        var recordCount = 0;
        for (var index = WordPerfectSignature.Length; index < bytes.Length - 2 && recordCount < MaximumRecords; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int valueOffset;
            if (bytes[index] == 0x00 && bytes[index + 1] == 0x98)
            {
                valueOffset = index + 2;
            }
            else if (index <= bytes.Length - 7 &&
                     bytes[index] == 0x00 && bytes[index + 1] == 0x01 && bytes[index + 2] == 0x00 &&
                     bytes[index + 3] != 0x00 && bytes[index + 4] == 0x00 && bytes[index + 5] == 0x01 &&
                     bytes[index + 6] == 0x00)
            {
                valueOffset = index + 7;
            }
            else
            {
                continue;
            }

            if (!TryReadUtf16Record(bytes, valueOffset, out var value, out var consumed))
            {
                continue;
            }

            AddWordPerfectValue(value, artifact.ArtifactId, recordCount, observations);
            recordCount++;
            index = Math.Max(index, valueOffset + consumed - 1);
        }

        if (recordCount == MaximumRecords)
        {
            diagnostics.Add(new("wordperfect.record_budget", "WordPerfect metadata record budget reached.", true));
        }

        StructuredMetadataUtilities.AddBinaryIndicators(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "wordperfect/binary",
            observations);
        StructuredMetadataUtilities.AddXmpPackets(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "wordperfect",
            observations,
            diagnostics);
        StructuredMetadataUtilities.FindAndAddEmbeddedImages(
            bytes,
            artifact.ArtifactId,
            Id,
            Version,
            "wordperfect",
            observations,
            diagnostics);

        return new ExtractionResult(
            observations.DistinctBy(item => (item.Category, item.NormalizedValue, item.Location)).ToArray(),
            [],
            diagnostics);
    }

    private static bool TryReadUtf16Record(byte[] bytes, int offset, out string value, out int consumed)
    {
        value = string.Empty;
        consumed = 0;
        if (offset < 0 || offset >= bytes.Length - 1)
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var index = offset; index <= bytes.Length - 2 && builder.Length < MaximumRecordCharacters; index += 2)
        {
            var character = (char)(bytes[index] | (bytes[index + 1] << 8));
            consumed += 2;
            if (character == '\0')
            {
                break;
            }

            if (char.IsControl(character) && character is not '\t')
            {
                return false;
            }

            builder.Append(character);
        }

        value = builder.ToString().Trim();
        return value.Length is > 1 and <= MaximumRecordCharacters && value.Any(char.IsLetterOrDigit);
    }

    private static void AddWordPerfectValue(
        string value,
        Guid artifactId,
        int record,
        ICollection<MetadataObservation> observations)
    {
        var lower = value.ToLowerInvariant();
        var category = LooksLikePath(value) ? "path" :
            lower.Contains("printer", StringComparison.Ordinal) ||
            lower.Contains("laserjet", StringComparison.Ordinal) ||
            lower.Contains("lexmark", StringComparison.Ordinal) ||
            lower.Contains("xerox", StringComparison.Ordinal) ||
            lower.Contains("epson", StringComparison.Ordinal) ? "printer" :
            lower.Contains("acrobat", StringComparison.Ordinal) ||
            lower.Contains("adobe", StringComparison.Ordinal) ||
            lower.Contains("writer", StringComparison.Ordinal) ||
            lower.Contains("converter", StringComparison.Ordinal) ? "application" : "legacy_string";

        observations.Add(MetadataUtilities.Observation(
            artifactId,
            category,
            value,
            "builtin.wordperfect",
            "1.0.0",
            $"wordperfect/record:{record}",
            category == "legacy_string" ? 0.6 : 0.88));

        var user = ExtractUserFromPath(value);
        if (!string.IsNullOrWhiteSpace(user))
        {
            observations.Add(MetadataUtilities.Observation(
                artifactId,
                "username",
                user,
                "builtin.wordperfect",
                "1.0.0",
                $"wordperfect/record:{record}/path-user",
                0.85));
        }
    }

    private static bool LooksLikePath(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\') ||
        value.StartsWith("\\\\", StringComparison.Ordinal);

    private static string? ExtractUserFromPath(string value)
    {
        var normalized = value.Replace('/', '\\');
        var marker = normalized.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            marker = normalized.IndexOf("\\Documents and Settings\\", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return null;
            }

            marker += "\\Documents and Settings\\".Length;
        }
        else
        {
            marker += "\\Users\\".Length;
        }

        var end = normalized.IndexOf('\\', marker);
        return end > marker ? normalized[marker..end] : null;
    }
}
