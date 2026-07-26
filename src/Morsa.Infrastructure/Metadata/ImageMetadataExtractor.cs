using MetadataExtractor;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Reads EXIF, IPTC and XMP directories without decoding image pixels.</summary>
public sealed class ImageMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.image";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Image];

    public ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var diagnostics = new List<ExtractionDiagnostic>();

        try
        {
            foreach (var directory in ImageMetadataReader.ReadMetadata(artifact.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var tag in directory.Tags)
                {
                    var value = tag.Description;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    observations.Add(MetadataUtilities.Observation(
                        artifact.ArtifactId,
                        MapCategory(tag.Name),
                        value,
                        Id,
                        Version,
                        $"{directory.Name}/{tag.Name}"));
                }
            }
        }
        catch (ImageProcessingException exception)
        {
            diagnostics.Add(new("image.invalid", exception.Message, true));
        }
        catch (IOException exception)
        {
            diagnostics.Add(new("image.io", exception.Message, true));
        }

        return ValueTask.FromResult(new ExtractionResult(observations, [], diagnostics));
    }

    private static string MapCategory(string tagName)
    {
        if (tagName.Contains("GPS", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Latitude", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Longitude", StringComparison.OrdinalIgnoreCase))
        {
            return "gps";
        }

        if (tagName.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Time", StringComparison.OrdinalIgnoreCase))
        {
            return "date";
        }

        if (tagName.Contains("Software", StringComparison.OrdinalIgnoreCase))
        {
            return "application";
        }

        if (tagName.Contains("Artist", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Creator", StringComparison.OrdinalIgnoreCase))
        {
            return "author";
        }

        return $"image.{tagName.ToLowerInvariant().Replace(' ', '_')}";
    }
}

