using System.IO.Compression;
using System.Xml;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Extracts OOXML and ODF properties with DTD and decompression budgets disabled.</summary>
public sealed class ZipXmlMetadataExtractor : IArtifactExtractor
{
    private const string ExtractorId = "builtin.zip-xml";

    private static readonly IReadOnlyDictionary<string, string> ElementCategories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["creator"] = "author",
            ["lastModifiedBy"] = "last_saved_by",
            ["created"] = "date",
            ["modified"] = "date",
            ["lastPrinted"] = "date",
            ["revision"] = "revision",
            ["title"] = "title",
            ["subject"] = "subject",
            ["description"] = "comments",
            ["keywords"] = "keywords",
            ["Application"] = "application",
            ["AppVersion"] = "application_version",
            ["Company"] = "company",
            ["Manager"] = "manager",
            ["generator"] = "application",
            ["editing-duration"] = "editing_duration",
            ["editing-cycles"] = "revision",
            ["initial-creator"] = "author",
            ["printed-by"] = "last_saved_by",
            ["creator-tool"] = "application",
            ["language"] = "language",
            ["template"] = "path",
            ["date-time"] = "date",
            ["comment"] = "comments",
        };

    public string Id => ExtractorId;

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } =
        [ArtifactKind.OpenXml, ArtifactKind.OpenDocument, ArtifactKind.Zip];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var diagnostics = new List<ExtractionDiagnostic>();
        long uncompressed = 0;
        var entriesRead = 0;

        try
        {
            using var archive = ZipFile.OpenRead(artifact.Path);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entriesRead++;
                if (entriesRead > options.MaxContainerEntries)
                {
                    diagnostics.Add(new("zip.entry_budget", "Container entry budget reached.", true));
                    break;
                }

                uncompressed = checked(uncompressed + entry.Length);
                if (uncompressed > options.MaxUncompressedBytes)
                {
                    diagnostics.Add(new("zip.size_budget", "Uncompressed byte budget reached.", true));
                    break;
                }

                if (!MetadataUtilities.IsSafeZipPath(entry.FullName))
                {
                    diagnostics.Add(new("zip.path_traversal", $"Unsafe entry rejected: {entry.FullName}", true));
                    continue;
                }

                if (!IsMetadataEntry(entry.FullName) || entry.Length > 8 * 1024 * 1024)
                {
                    continue;
                }

                if ((entry.Length > 0 && entry.CompressedLength == 0) ||
                    (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1_000))
                {
                    diagnostics.Add(new("zip.compression_ratio", $"Suspicious compression ratio rejected: {entry.FullName}", true));
                    continue;
                }

                await using var stream = entry.Open();
                await ReadXmlAsync(stream, entry.FullName, artifact.ArtifactId, observations, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new("zip.invalid", exception.Message, true));
        }
        catch (XmlException exception)
        {
            diagnostics.Add(new("xml.invalid", exception.Message, true));
        }

        return new ExtractionResult(observations, [], diagnostics);
    }

    private static bool IsMetadataEntry(string name) =>
        name.Equals("docProps/core.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("docProps/app.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("docProps/custom.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("meta.xml", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("VersionList.xml", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("versions/", StringComparison.OrdinalIgnoreCase);

    private static async Task ReadXmlAsync(
        Stream stream,
        string entryName,
        Guid artifactId,
        ICollection<MetadataObservation> observations,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
            MaxCharactersFromEntities = 0,
        };

        using var reader = XmlReader.Create(stream, settings);
        string? customProperty = null;
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observations.Count >= 100_000) return;
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement)
            {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }

            var localName = reader.LocalName;
            if (entryName.Equals("docProps/custom.xml", StringComparison.OrdinalIgnoreCase) && localName == "property")
            {
                customProperty = reader.GetAttribute("name");
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }
            if (localName == "Relationship")
            {
                var target = reader.GetAttribute("Target");
                if (!string.IsNullOrWhiteSpace(target))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        Uri.TryCreate(target, UriKind.Absolute, out _) ? "url" : LooksLikePath(target) ? "path" : "external_relationship",
                        target,
                        ExtractorId,
                        "1.0.0",
                        $"{entryName}@Target"));
                }

                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }

            if (customProperty is not null && entryName.Equals("docProps/custom.xml", StringComparison.OrdinalIgnoreCase))
            {
                var customValue = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(customValue))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifactId, $"custom.{Slug(customProperty)}", customValue, ExtractorId, "1.0.0", $"{entryName}/{customProperty}", 0.9));
                }
                customProperty = null;
                continue;
            }

            if (!ElementCategories.TryGetValue(localName, out var category))
            {
                await reader.ReadAsync().ConfigureAwait(false);
                continue;
            }

            var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    category,
                    value,
                    ExtractorId,
                    "1.0.0",
                    $"{entryName}/{localName}"));
            }
            // ReadElementContentAsStringAsync already moves to the following node.
        }
    }

    private static bool LooksLikePath(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        (value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':') ||
        value.StartsWith("../", StringComparison.Ordinal) || value.StartsWith("/", StringComparison.Ordinal);

    private static string Slug(string value) => new(value.Trim().ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
}
