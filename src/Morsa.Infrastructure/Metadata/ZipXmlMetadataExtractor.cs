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
        name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

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
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement)
            {
                continue;
            }

            var localName = reader.LocalName;
            if (localName == "Relationship")
            {
                var target = reader.GetAttribute("Target");
                if (!string.IsNullOrWhiteSpace(target))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        Uri.TryCreate(target, UriKind.Absolute, out _) ? "url" : "external_relationship",
                        target,
                        ExtractorId,
                        "1.0.0",
                        $"{entryName}@Target"));
                }

                continue;
            }

            if (!ElementCategories.TryGetValue(localName, out var category))
            {
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
        }
    }
}

