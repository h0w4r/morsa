using Morsa.Domain.Common;

namespace Morsa.Application.Abstractions;

/// <summary>Result of securely storing a local or downloaded artifact.</summary>
public sealed record StoredArtifact(
    string Path,
    string Sha256,
    long Size,
    ArtifactKind Kind,
    string? MimeType);

/// <summary>Content-addressable storage for hostile inputs.</summary>
public interface IArtifactStorage
{
    Task<StoredArtifact> StoreAsync(
        Stream source,
        string? suggestedName,
        long maximumBytes,
        CancellationToken cancellationToken);
}

/// <summary>Inspects magic bytes without trusting a user-controlled extension.</summary>
public interface IArtifactInspector
{
    Task<(ArtifactKind Kind, string? MimeType)> InspectAsync(
        string path,
        CancellationToken cancellationToken);
}

