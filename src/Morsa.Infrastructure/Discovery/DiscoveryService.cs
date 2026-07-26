using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Domain.Discovery;

namespace Morsa.Infrastructure.Discovery;

/// <summary>Fans out providers, persists cursors and merges duplicate URLs with provenance.</summary>
public sealed class DiscoveryService(IEnumerable<ISearchProvider> providers, IMorsaStore store)
{
    public async Task<(int Added, IReadOnlyList<string> FailedProviders)> DiscoverAsync(
        Guid projectId,
        Guid runId,
        SearchQuery query,
        SearchExecutionContext context,
        IReadOnlyCollection<string>? requestedProviders,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var existing = await store.DiscoveredResources.Where(item => item.ProjectId == projectId)
            .Select(item => item.CanonicalUrl).ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);
        var added = 0;
        foreach (var provider in providers.Where(provider => requestedProviders is null || requestedProviders.Contains(provider.Id)))
        {
            var journal = new ProviderRequest { RunId = runId, ProviderId = provider.Id, Query = query.Target, Status = "running" };
            store.Add(journal);
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var result in provider.SearchAsync(query, context, cancellationToken).ConfigureAwait(false))
                {
                    var canonical = DiscoveryUtilities.Canonicalize(result.Url);
                    if (!existing.Add(canonical))
                    {
                        continue;
                    }

                    store.Add(new DiscoveredResource
                    {
                        ProjectId = projectId,
                        RunId = runId,
                        Url = result.Url,
                        CanonicalUrl = canonical,
                        ProviderId = result.ProviderId,
                        Query = result.Query,
                        Title = result.Title,
                        Snippet = result.Snippet,
                        DiscoveredAt = result.DiscoveredAt,
                    });
                    added++;
                }

                journal.Status = "completed";
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
            {
                journal.Status = "failed";
                journal.LastError = exception.Message;
                journal.AttemptCount++;
                failures.Add(provider.Id);
            }

            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (added, failures);
    }
}
