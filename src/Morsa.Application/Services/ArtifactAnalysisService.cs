using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;

namespace Morsa.Application.Services;

/// <summary>Coordinates extraction and persists only validated neutral observations.</summary>
public sealed class ArtifactAnalysisService(
    IMorsaStore store,
    IArtifactExtractorRegistry extractors)
{
    public async Task<ExtractionResult> AnalyzeAsync(
        Artifact artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var extractor = extractors.Select(artifact.Kind);
        if (extractor is null)
        {
            return new ExtractionResult(
                [],
                [],
                [new ExtractionDiagnostic("artifact.unsupported", $"No extractor supports {artifact.Kind}.", true)]);
        }

        var context = new ArtifactContext(
            artifact.Id,
            artifact.StoredPath,
            artifact.Sha256,
            artifact.Kind,
            artifact.MimeType);

        var result = await extractor.ExtractAsync(context, options, cancellationToken).ConfigureAwait(false);

        // The application layer owns persistence so extractors cannot partially commit.
        store.AddRange(result.Observations.Where(IsValid));
        store.AddRange(result.Findings);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static bool IsValid(MetadataObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.Category) &&
        !string.IsNullOrWhiteSpace(observation.OriginalValue) &&
        observation.Confidence is >= 0 and <= 1;
}

