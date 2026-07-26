// SPDX-License-Identifier: GPL-3.0-or-later
// Selective clean port of FOCA Office972003.cs and OleDocument.cs at commit 754453ad7f9579a6021c484d5014a3cd12fd0e35.
using System.Buffers.Binary;
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using OpenMcdf;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Reads bounded OLE property sets and content streams using the maintained OpenMcdf parser.</summary>
public sealed class OleMetadataExtractor : IArtifactExtractor
{
    private static readonly byte[] SummaryInformationFormatId =
        [0xe0, 0x85, 0x9f, 0xf2, 0xf9, 0x4f, 0x68, 0x10, 0xab, 0x91, 0x08, 0x00, 0x2b, 0x27, 0xb3, 0xd9];
    private static readonly byte[] DocumentSummaryInformationFormatId =
        [0x02, 0xd5, 0xcd, 0xd5, 0x9c, 0x2e, 0x1b, 0x10, 0x93, 0x97, 0x08, 0x00, 0x2b, 0x2c, 0xf9, 0xae];
    private static readonly byte[] CustomInformationFormatId =
        [0x05, 0xd5, 0xcd, 0xd5, 0x9c, 0x2e, 0x1b, 0x10, 0x93, 0x97, 0x08, 0x00, 0x2b, 0x2c, 0xf9, 0xae];

    public string Id => "builtin.ole";
    public string Version => "1.0.0";
    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.OleCompound];

    public ValueTask<ExtractionResult> ExtractAsync(ArtifactContext artifact, ExtractionOptions options, CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var diagnostics = new List<ExtractionDiagnostic>();
        var budget = new StreamBudget(Math.Min(options.MaxUncompressedBytes, 256 * 1024 * 1024), options.MaxContainerEntries);
        try
        {
            using var root = RootStorage.OpenRead(artifact.Path);
            VisitStorage(root, "/", 0, artifact.ArtifactId, options, budget, observations, diagnostics, cancellationToken);
            ReadWordRevisionHistory(root, artifact.ArtifactId, options, observations, diagnostics);
        }
        catch (Exception exception) when (exception is OpenMcdf.FileFormatException or IOException or InvalidDataException)
        {
            diagnostics.Add(new("ole.invalid", exception.Message, true));
        }
        return ValueTask.FromResult(new ExtractionResult(
            observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], diagnostics));
    }

    private static void VisitStorage(
        Storage storage,
        string path,
        int depth,
        Guid artifactId,
        ExtractionOptions options,
        StreamBudget budget,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (depth > options.MaxDepth)
        {
            diagnostics.Add(new("ole.depth_budget", $"OLE storage depth exceeded at {path}.", true));
            return;
        }
        foreach (var entry in storage.EnumerateEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++budget.Entries > budget.MaximumEntries)
            {
                diagnostics.Add(new("ole.entry_budget", "OLE entry budget reached.", true));
                return;
            }
            if (entry.Type == EntryType.Storage)
            {
                var child = storage.OpenStorage(entry.Name);
                VisitStorage(child, $"{path}{entry.Name}/", depth + 1, artifactId, options, budget, observations, diagnostics, cancellationToken);
                continue;
            }
            if (entry.Length < 0 || entry.Length > Math.Min(options.MaxBytes, 64 * 1024 * 1024))
            {
                diagnostics.Add(new("ole.stream_budget", $"OLE stream rejected by size budget: {path}{entry.Name}.", true));
                continue;
            }
            budget.Bytes = checked(budget.Bytes + entry.Length);
            if (budget.Bytes > budget.MaximumBytes)
            {
                diagnostics.Add(new("ole.total_budget", "OLE aggregate stream budget reached.", true));
                return;
            }
            using var stream = storage.OpenStream(entry.Name);
            var bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            var location = $"ole:{path}{entry.Name}";
            if (entry.Name.EndsWith("SummaryInformation", StringComparison.Ordinal))
                ReadPropertySet(bytes, entry.Name.Contains("DocumentSummary", StringComparison.Ordinal), artifactId, location, observations, diagnostics);
            if (entry.Name.EndsWith("Ole10Native", StringComparison.OrdinalIgnoreCase))
                ReadOle10Native(bytes, artifactId, location, observations, diagnostics);
            StructuredMetadataUtilities.AddBinaryIndicators(bytes, artifactId, "builtin.ole", "1.0.0", location, observations);
            StructuredMetadataUtilities.AddXmpPackets(bytes, artifactId, "builtin.ole", "1.0.0", location, observations, diagnostics);
            StructuredMetadataUtilities.FindAndAddEmbeddedImages(bytes, artifactId, "builtin.ole", "1.0.0", location, observations, diagnostics);
        }
    }

    private static void ReadPropertySet(
        byte[] data,
        bool documentSummary,
        Guid artifactId,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        try
        {
            Ensure(data, 0, 32);
            if (BinaryPrimitives.ReadUInt16LittleEndian(data) != 0xfffe) throw new InvalidDataException("OLE property set byte order is invalid.");
            var operatingSystem = MapOperatingSystem(data[4], data[5]);
            if (operatingSystem is not null)
            {
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    "operating_system",
                    operatingSystem,
                    "builtin.ole",
                    "1.0.0",
                    $"{location}/header",
                    0.85));
            }
            var setCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24, 4));
            if (setCount is 0 or > 16) throw new InvalidDataException("OLE property set count is invalid.");
            for (var set = 0; set < setCount; set++)
            {
                var descriptor = 28 + (set * 20);
                Ensure(data, descriptor, 20);
                var setKind = GetPropertySetKind(data.AsSpan(descriptor, 16), documentSummary);
                var section = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descriptor + 16, 4)));
                Ensure(data, section, 8);
                var propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section + 4, 4));
                if (propertyCount > 10_000) throw new InvalidDataException("OLE property count exceeds budget.");

                var entries = new List<(uint Id, int Offset)>(checked((int)propertyCount));
                for (var index = 0; index < propertyCount; index++)
                {
                    var table = section + 8 + (index * 8);
                    Ensure(data, table, 8);
                    var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(table, 4));
                    var offset = section + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(table + 4, 4)));
                    entries.Add((id, offset));
                }

                var encoding = ResolvePropertyEncoding(data, entries);
                var customNames = setKind == PropertySetKind.Custom
                    ? ReadCustomPropertyNames(data, entries, encoding)
                    : [];
                foreach (var (id, offset) in entries)
                {
                    if (setKind == PropertySetKind.Custom && id == 0) continue;
                    if (!TryReadProperty(data, offset, encoding, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                    var category = setKind == PropertySetKind.Custom
                        ? MapCustomProperty(customNames.GetValueOrDefault(id), id)
                        : MapProperty(setKind == PropertySetKind.DocumentSummary, id);
                    if (category is null) continue;
                    observations.Add(MetadataUtilities.Observation(artifactId, category, value, "builtin.ole", "1.0.0", $"{location}/property:{id}", 0.95));
                    if (category == "template" && LooksLikePath(value))
                    {
                        observations.Add(MetadataUtilities.Observation(
                            artifactId,
                            "path",
                            value,
                            "builtin.ole",
                            "1.0.0",
                            $"{location}/property:{id}/template-path",
                            0.9));
                    }
                }
            }
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("ole.property_set_invalid", $"{location}: {exception.Message}", true));
        }
    }

    private static bool TryReadProperty(byte[] data, int offset, out string value) =>
        TryReadProperty(data, offset, Encoding.Latin1, out value);

    private static bool TryReadProperty(byte[] data, int offset, Encoding encoding, out string value)
    {
        value = string.Empty;
        Ensure(data, offset, 4);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) & 0xffff;
        offset += 4;
        switch (type)
        {
            case 0x1e: // VT_LPSTR
                Ensure(data, offset, 4);
                var byteCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
                Ensure(data, offset + 4, byteCount);
                value = encoding.GetString(data, offset + 4, Math.Max(0, byteCount - 1)).TrimEnd('\0');
                return true;
            case 0x1f: // VT_LPWSTR
                Ensure(data, offset, 4);
                var chars = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
                var bytes = checked(chars * 2);
                Ensure(data, offset + 4, bytes);
                value = Encoding.Unicode.GetString(data, offset + 4, Math.Max(0, bytes - 2)).TrimEnd('\0');
                return true;
            case 0x02:
                Ensure(data, offset, 2); value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case 0x03:
                Ensure(data, offset, 4); value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case 0x13:
                Ensure(data, offset, 4); value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case 0x14:
                Ensure(data, offset, 8); value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8)).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case 0x15:
                Ensure(data, offset, 8); value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case 0x0b: // VT_BOOL uses a 16-bit VARIANT_BOOL.
                Ensure(data, offset, 2); value = (BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) != 0).ToString(); return true;
            case 0x08: // VT_BSTR stores its byte length rather than a character count.
                Ensure(data, offset, 4);
                var bstrBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
                Ensure(data, offset + 4, bstrBytes);
                value = Encoding.Unicode.GetString(data, offset + 4, bstrBytes).TrimEnd('\0');
                return true;
            case 0x40: // VT_FILETIME
                Ensure(data, offset, 8);
                var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
                if (fileTime <= 0) return false;
                value = DateTimeOffset.FromFileTime(fileTime).ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static PropertySetKind GetPropertySetKind(ReadOnlySpan<byte> formatId, bool documentSummaryFallback)
    {
        if (formatId.SequenceEqual(SummaryInformationFormatId)) return PropertySetKind.Summary;
        if (formatId.SequenceEqual(DocumentSummaryInformationFormatId)) return PropertySetKind.DocumentSummary;
        if (formatId.SequenceEqual(CustomInformationFormatId)) return PropertySetKind.Custom;
        return documentSummaryFallback ? PropertySetKind.DocumentSummary : PropertySetKind.Summary;
    }

    private static Encoding ResolvePropertyEncoding(byte[] data, IReadOnlyCollection<(uint Id, int Offset)> entries)
    {
        var codePageEntry = entries.FirstOrDefault(entry => entry.Id == 1);
        if (codePageEntry == default)
        {
            return Encoding.Latin1;
        }

        Ensure(data, codePageEntry.Offset, 6);
        var propertyType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(codePageEntry.Offset, 4));
        int codePage;
        if (propertyType == 0x02)
        {
            // PID_CODEPAGE is serialized as VT_I2 but its 16-bit value represents an unsigned code-page identifier.
            codePage = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(codePageEntry.Offset + 4, 2));
        }
        else if (!TryReadProperty(data, codePageEntry.Offset, out var codePageValue) ||
                 !int.TryParse(codePageValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out codePage))
        {
            return Encoding.Latin1;
        }

        if (codePage == 1200) return Encoding.Unicode;
        if (codePage == 65001) return new UTF8Encoding(false, throwOnInvalidBytes: false);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1;
        }
    }

    private static Dictionary<uint, string> ReadCustomPropertyNames(
        byte[] data,
        IReadOnlyCollection<(uint Id, int Offset)> entries,
        Encoding encoding)
    {
        var result = new Dictionary<uint, string>();
        var dictionaryEntry = entries.FirstOrDefault(entry => entry.Id == 0);
        if (dictionaryEntry == default) return result;
        var cursor = dictionaryEntry.Offset;
        Ensure(data, cursor, 4);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += 4;
        if (count > 10_000) throw new InvalidDataException("OLE custom property dictionary exceeds its budget.");
        for (var index = 0; index < count; index++)
        {
            Ensure(data, cursor, 8);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
            var characters = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4, 4)));
            cursor += 8;
            if (characters is < 1 or > 16_384) throw new InvalidDataException("OLE custom property name length is invalid.");
            var byteCount = checked(characters * (encoding.CodePage == Encoding.Unicode.CodePage ? 2 : 1));
            Ensure(data, cursor, byteCount);
            var name = encoding.GetString(data, cursor, byteCount).TrimEnd('\0');
            // DictionaryEntry packets are contiguous; only the complete Dictionary packet has trailing 4-byte padding.
            cursor += byteCount;
            if (!string.IsNullOrWhiteSpace(name)) result[id] = name;
        }

        return result;
    }

    private static string MapCustomProperty(string? name, uint id)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"custom.property_{id}";
        var normalized = new string(name.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray()).Trim('_');
        return normalized switch
        {
            "author" or "creator" => "author",
            "company" => "company",
            "manager" => "manager",
            "application" or "software" => "application",
            "path" or "filepath" or "file_path" => "path",
            "title" => "title",
            "comments" or "description" => "comments",
            _ => $"custom.{(string.IsNullOrWhiteSpace(normalized) ? $"property_{id}" : normalized)}",
        };
    }

    private static string? MapProperty(bool documentSummary, uint id) => documentSummary
        ? id switch
        {
            2 => "category",
            3 => "presentation_target",
            4 => "byte_count",
            5 => "line_count",
            6 => "paragraph_count",
            7 => "slide_count",
            8 => "note_count",
            9 => "hidden_slide_count",
            10 => "multimedia_clip_count",
            11 => "scale_crop",
            14 => "manager",
            15 => "company",
            16 => "links_up_to_date",
            _ => null,
        }
        : id switch
        {
            2 => "title",
            3 => "subject",
            4 => "author",
            5 => "keywords",
            6 => "comments",
            7 => "template",
            8 => "last_saved_by",
            9 => "revision",
            10 => "editing_time",
            11 => "last_printed_date",
            12 or 13 => "date",
            14 => "page_count",
            15 => "word_count",
            16 => "character_count",
            18 => "application",
            19 => "security",
            _ => null,
        };

    /// <summary>Reads the Ole10Native envelope without writing or launching the embedded payload.</summary>
    private static void ReadOle10Native(
        byte[] bytes,
        Guid artifactId,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        try
        {
            Ensure(bytes, 0, 6);
            var cursor = 6; // Total size followed by the first two-byte flags field.
            var label = ReadNullTerminated(bytes, ref cursor, 4_096);
            var originalPath = ReadNullTerminated(bytes, ref cursor, 16_384);
            Ensure(bytes, cursor, 4);
            cursor += 4; // Reserved flags used by the OLE native wrapper.
            var temporaryPath = ReadNullTerminated(bytes, ref cursor, 16_384);

            AddValue("embedded_object", label, "label");
            AddValue(LooksLikePath(originalPath) ? "path" : "embedded_object", originalPath, "original-path");
            AddValue("path", temporaryPath, "temporary-path");

            if (cursor <= bytes.Length - 4)
            {
                var nativeLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4)));
                cursor += 4;
                Ensure(bytes, cursor, nativeLength);
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    "embedded_object",
                    $"native-payload:{nativeLength}",
                    "builtin.ole",
                    "1.0.0",
                    $"{location}/payload",
                    1.0));
                StructuredMetadataUtilities.FindAndAddEmbeddedImages(
                    bytes.AsMemory(cursor, nativeLength),
                    artifactId,
                    "builtin.ole",
                    "1.0.0",
                    $"{location}/payload",
                    observations,
                    diagnostics);
            }

            void AddValue(string category, string value, string suffix)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    category,
                    value,
                    "builtin.ole",
                    "1.0.0",
                    $"{location}/{suffix}",
                    0.95));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            diagnostics.Add(new("ole.native_invalid", $"{location}: {exception.Message}", false));
        }
    }

    /// <summary>Ports the bounded Word revision-table extraction used by FOCA's Office972003 parser.</summary>
    private static void ReadWordRevisionHistory(
        Storage root,
        Guid artifactId,
        ExtractionOptions options,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        try
        {
            if (!root.TryOpenStream("WordDocument", out var wordDocument)) return;
            using (wordDocument)
            {
                if (wordDocument.Length < 0x2da || wordDocument.Length > Math.Min(options.MaxBytes, 64L * 1024 * 1024)) return;
                var word = new byte[checked((int)wordDocument.Length)];
                wordDocument.ReadExactly(word);
                var tableName = (word[0x0b] & 0x02) == 0x02 ? "1Table" : "0Table";
                var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(0x2d2, 4));
                var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(0x2d6, 4));
                if (tableSize == 0 || tableSize > 4 * 1024 * 1024 || !root.TryOpenStream(tableName, out var tableStream)) return;

                using (tableStream)
                {
                    if (tableOffset > tableStream.Length || tableSize > tableStream.Length - tableOffset) return;
                    tableStream.Position = tableOffset;
                    var table = new byte[checked((int)tableSize)];
                    tableStream.ReadExactly(table);
                    ReadRevisionTable(table, artifactId, observations, diagnostics);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            diagnostics.Add(new("ole.history_invalid", $"Word revision history could not be parsed: {exception.Message}", false));
        }
    }

    private static void ReadRevisionTable(
        byte[] table,
        Guid artifactId,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        Ensure(table, 0, 6);
        var cursor = 0;
        var unicode = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(cursor, 2)) == 0xffff;
        cursor += 2;
        var stringCount = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(cursor, 2));
        cursor += 2;
        var extraBytes = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(cursor, 2));
        cursor += 2;
        if (stringCount > 4_096 || extraBytes > 4_096)
        {
            diagnostics.Add(new("ole.history_budget", "Word revision history count exceeds its budget.", true));
            return;
        }

        for (var index = 0; index + 1 < stringCount; index += 2)
        {
            if (!TryReadLengthPrefixedString(table, ref cursor, unicode, out var author) ||
                !TryReadLengthPrefixedString(table, ref cursor, unicode, out var path))
            {
                diagnostics.Add(new("ole.history_truncated", "Word revision history is truncated.", false));
                return;
            }

            if (extraBytes > 0)
            {
                Ensure(table, cursor, extraBytes);
                cursor += extraBytes;
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    "author",
                    author,
                    "builtin.ole",
                    "1.0.0",
                    $"ole:/WordDocument/history:{index / 2}/author",
                    0.9));
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    "history.author",
                    author,
                    "builtin.ole",
                    "1.0.0",
                    $"ole:/WordDocument/history:{index / 2}",
                    0.9));
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    LooksLikePath(path) ? "path" : "history.path",
                    path,
                    "builtin.ole",
                    "1.0.0",
                    $"ole:/WordDocument/history:{index / 2}/path",
                    0.9));
            }
        }
    }

    private static bool TryReadLengthPrefixedString(byte[] data, ref int cursor, bool unicode, out string value)
    {
        value = string.Empty;
        if (cursor > data.Length - 2) return false;
        var characterCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(cursor, 2));
        cursor += 2;
        var byteCount = checked(characterCount * (unicode ? 2 : 1));
        if (characterCount > 16_384 || cursor > data.Length - byteCount) return false;
        value = (unicode ? Encoding.Unicode : Encoding.Latin1)
            .GetString(data, cursor, byteCount)
            .TrimEnd('\0', ' ');
        cursor += byteCount;
        return true;
    }

    private static string ReadNullTerminated(byte[] data, ref int cursor, int maximumBytes)
    {
        var start = cursor;
        while (cursor < data.Length && cursor - start <= maximumBytes && data[cursor] != 0) cursor++;
        if (cursor >= data.Length || cursor - start > maximumBytes)
            throw new InvalidDataException("OLE native string is unterminated or over budget.");
        var value = Encoding.Latin1.GetString(data, start, cursor - start);
        cursor++;
        return value;
    }

    private static string? MapOperatingSystem(byte high, byte low) => (high, low) switch
    {
        (1, 0) => "OpenOffice",
        (3, 10) => "Mac OS",
        (3, 51) => "Windows NT 3.51",
        (4, 0) => "Windows NT 4.0",
        (4, 10) => "Windows 98",
        (5, 0) => "Windows 2000",
        (5, 1) => "Windows XP",
        (5, 2) => "Windows Server 2003",
        (6, 0) => "Windows Vista",
        (6, 1) => "Windows 7",
        _ => null,
    };

    private static bool LooksLikePath(string value) =>
        (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\') ||
        value.StartsWith("\\\\", StringComparison.Ordinal);

    private static void Ensure(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length) throw new InvalidDataException("OLE property data is truncated.");
    }

    private sealed class StreamBudget(long maximumBytes, int maximumEntries)
    {
        public long MaximumBytes { get; } = maximumBytes;
        public int MaximumEntries { get; } = maximumEntries;
        public long Bytes { get; set; }
        public int Entries { get; set; }
    }

    private enum PropertySetKind
    {
        Summary,
        DocumentSummary,
        Custom,
    }

}
