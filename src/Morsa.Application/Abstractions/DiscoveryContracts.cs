namespace Morsa.Application.Abstractions;

/// <summary>Provider-neutral document discovery request.</summary>
public sealed record SearchQuery(
    string Target,
    IReadOnlyCollection<string> FileTypes,
    string? Cursor = null,
    int MaxResults = 100);

/// <summary>Execution policy and budgets visible to a search provider.</summary>
public sealed record SearchExecutionContext(
    Guid? RunId,
    Guid? TaskId,
    string SessionKey,
    string? ProxyPool,
    int QueryBudget);

/// <summary>Provider result with source provenance.</summary>
public sealed record SearchResult(
    string Url,
    string? Title,
    string? Snippet,
    string ProviderId,
    string Query,
    DateTimeOffset DiscoveredAt);

/// <summary>Health probe result used by automatic provider selection.</summary>
public sealed record ProviderHealth(bool IsHealthy, string State, string? Detail = null);

/// <summary>Replaceable web search, archive, crawler or import provider.</summary>
public interface ISearchProvider
{
    string Id { get; }

    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        CancellationToken cancellationToken);
}

