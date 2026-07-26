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
            if (new FileInfo(artifact.Path).Length > Math.Min(options.MaxBytes, 100L * 1024 * 1024))
                return ValueTask.FromResult(new ExtractionResult([], [], [new("image.size_budget", "Image exceeds the parser byte budget.", true)]));
            foreach (var directory in ImageMetadataReader.ReadMetadata(artifact.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var tag in directory.Tags)
                {
                    if (observations.Count >= 100_000)
                    {
                        diagnostics.Add(new("image.observation_budget", "Image metadata observation budget reached.", true));
                        return ValueTask.FromResult(new ExtractionResult(observations, [], diagnostics));
                    }
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

        if (tagName.Contains("Host Computer", StringComparison.OrdinalIgnoreCase)) return "hostname";
        if (tagName.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return "operating_system";
        if (tagName.Contains("Model", StringComparison.OrdinalIgnoreCase)) return "device_model";
        if (tagName.Contains("Copyright", StringComparison.OrdinalIgnoreCase)) return "copyright";

        return $"image.{tagName.ToLowerInvariant().Replace(' ', '_')}";
    }
}
