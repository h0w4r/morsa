using Morsa.Domain.Common;

namespace Morsa.Domain.Artifacts;

/// <summary>Content-addressed artifact acquired from disk or a network source.</summary>
public sealed class Artifact : Entity
{
    public Guid RunId { get; set; }

    public string? SourceUri { get; set; }

    public string? OriginalPath { get; set; }

    public required string StoredPath { get; set; }

    public required string Sha256 { get; set; }

    public long Size { get; set; }

    public string? MimeType { get; set; }

    public ArtifactKind Kind { get; set; }

    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Neutral extractor output with provenance and a normalized value.</summary>
public sealed class MetadataObservation : Entity
{
    public Guid ArtifactId { get; set; }

    public required string Category { get; set; }

    public required string OriginalValue { get; set; }

    public required string NormalizedValue { get; set; }

    public required string Extractor { get; set; }

    public required string ExtractorVersion { get; set; }

    public string? Location { get; set; }

    public double Confidence { get; set; } = 1.0;

    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Evidence links a derived fact back to an immutable artifact location.</summary>
public sealed class Evidence : Entity
{
    public Guid ArtifactId { get; set; }

    public Guid? ObservationId { get; set; }

    public required string Source { get; set; }

    public required string Value { get; set; }

    public string? Location { get; set; }

    public required string ArtifactSha256 { get; set; }

    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Structured issue emitted by analysis, correlation or policy checks.</summary>
public sealed class Finding : Entity
{
    public Guid RunId { get; set; }

    public Guid? ArtifactId { get; set; }

    public required string RuleId { get; set; }

    public required string Title { get; set; }

    public required string Description { get; set; }

    public FindingSeverity Severity { get; set; }

    public double Confidence { get; set; } = 1.0;

    public bool Sensitive { get; set; }
}
