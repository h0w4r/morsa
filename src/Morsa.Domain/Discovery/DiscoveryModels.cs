using Morsa.Domain.Common;

namespace Morsa.Domain.Discovery;

/// <summary>URL candidate discovered by a provider, crawler or manifest.</summary>
public sealed class DiscoveredResource : Entity
{
    public Guid ProjectId { get; set; }
    public Guid? RunId { get; set; }
    public required string Url { get; set; }
    public required string CanonicalUrl { get; set; }
    public required string ProviderId { get; set; }
    public required string Query { get; set; }
    public string? Title { get; set; }
    public string? Snippet { get; set; }
    public string Status { get; set; } = "pending";
    public string? LastError { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Provider request journal including cursor and retry state.</summary>
public sealed class ProviderRequest : Entity
{
    public Guid RunId { get; set; }
    public required string ProviderId { get; set; }
    public required string Query { get; set; }
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public string? LastCursor { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? CoverageTagsJson { get; set; }
    public string? LastError { get; set; }
}

