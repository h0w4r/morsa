using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Application.Abstractions;

/// <summary>Input passed to a metadata extractor after MIME and magic-byte inspection.</summary>
public sealed record ArtifactContext(
    Guid ArtifactId,
    string Path,
    string Sha256,
    ArtifactKind Kind,
    string? MimeType);

/// <summary>Resource budgets enforced while parsing an untrusted artifact.</summary>
public sealed record ExtractionOptions(
    long MaxBytes = 100 * 1024 * 1024,
    long MaxUncompressedBytes = 500 * 1024 * 1024,
    int MaxContainerEntries = 10_000,
    int MaxDepth = 8,
    TimeSpan? Timeout = null);

/// <summary>Diagnostic emitted without failing the entire artifact pipeline.</summary>
public sealed record ExtractionDiagnostic(string Code, string Message, bool IsError);

/// <summary>Neutral extraction result persisted by the application layer.</summary>
public sealed record ExtractionResult(
    IReadOnlyList<MetadataObservation> Observations,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<ExtractionDiagnostic> Diagnostics);

/// <summary>Contract implemented by every content parser.</summary>
public interface IArtifactExtractor
{
    string Id { get; }

    string Version { get; }

    IReadOnlyCollection<ArtifactKind> SupportedKinds { get; }

    ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Selects an extractor from inspected artifact characteristics.</summary>
public interface IArtifactExtractorRegistry
{
    IArtifactExtractor? Select(ArtifactKind kind);

    IReadOnlyCollection<IArtifactExtractor> All { get; }
}

/// <summary>Boundary used by the application to parse hostile artifacts outside its process.</summary>
public interface IArtifactParserGateway
{
    Task<ExtractionResult> ParseAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken);
}
