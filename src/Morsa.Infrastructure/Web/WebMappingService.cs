using System.Text;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Infrastructure.Discovery;
using Morsa.Infrastructure.Networking;

namespace Morsa.Infrastructure.Web;

/// <summary>Bounded same-host crawler and evidence-driven backup candidate validator.</summary>
public sealed class WebMappingService(
    IMorsaStore store,
    RotatingHttpClient http,
    NetworkScopeValidator scopeValidator)
{
    public async Task<IReadOnlyList<DiscoveredResource>> CrawlAsync(
        Guid projectId,
        Guid runId,
        Uri root,
        int maximumDepth,
        int maximumPages,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(Uri Uri, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownResources = await store.DiscoveredResources
            .Where(item => item.ProjectId == projectId)
            .Select(item => item.CanonicalUrl)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);
        var discovered = new List<DiscoveredResource>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && visited.Count < maximumPages)
        {
            var (uri, depth) = queue.Dequeue();
            var canonical = DiscoveryUtilities.Canonicalize(uri.AbsoluteUri);
            if (!visited.Add(canonical))
            {
                continue;
            }

            var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
                projectId, uri, ActivityMode.Active, false, cancellationToken).ConfigureAwait(false);
            if (validatedAddresses is null) continue;

            HttpFetchResult result;
            try
            {
                result = await http.FetchPinnedAsync(
                    uri,
                    proxyPool,
                    new NetworkRequestContext(runId, null, $"web:{root.Host}", uri, "web-map", null),
                    4 * 1024 * 1024,
                    validatedAddresses,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            if (result.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            foreach (var link in DiscoveryUtilities.ExtractLinks(Encoding.UTF8.GetString(result.Content), uri))
            {
                var target = new Uri(link.Url);
                if (!target.IdnHost.Equals(root.IdnHost, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetCanonical = DiscoveryUtilities.Canonicalize(target.AbsoluteUri);
                var resource = new DiscoveredResource
                {
                    ProjectId = projectId,
                    RunId = runId,
                    Url = target.AbsoluteUri,
                    CanonicalUrl = targetCanonical,
                    ProviderId = "web-map",
                    Query = root.AbsoluteUri,
                    Title = link.Title,
                    Status = "observed",
                };
                if (knownResources.Add(targetCanonical))
                {
                    store.Add(resource);
                    discovered.Add(resource);
                }

                if (depth < maximumDepth && !visited.Contains(targetCanonical))
                {
                    queue.Enqueue((target, depth + 1));
                }
            }

            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return discovered;
    }

    public async Task<IReadOnlyList<Finding>> ValidateBackupCandidatesAsync(
        Guid projectId,
        Guid runId,
        Uri root,
        int maximumRequests,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var paths = await store.DiscoveredResources.Where(item => item.ProjectId == projectId)
            .Select(item => item.Url).ToListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = paths.SelectMany(CreateBackupCandidates)
            .Append(new Uri(root, "backup.zip"))
            .Append(new Uri(root, "www.tar.gz"))
            .DistinctBy(uri => uri.AbsoluteUri)
            .Take(maximumRequests)
            .ToArray();
        var findings = new List<Finding>();
        foreach (var candidate in candidates)
        {
            var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
                projectId, candidate, ActivityMode.Aggressive, false, cancellationToken).ConfigureAwait(false);
            if (validatedAddresses is null)
            {
                continue;
            }
            try
            {
                var result = await http.FetchPinnedAsync(
                    candidate,
                    proxyPool,
                    new NetworkRequestContext(runId, null, $"backup:{root.Host}", candidate, "backup-fuzz", null),
                    64 * 1024,
                    validatedAddresses,
                    cancellationToken).ConfigureAwait(false);
                if ((int)result.StatusCode is >= 200 and < 300)
                {
                    findings.Add(new Finding
                    {
                        RunId = runId,
                        RuleId = "web.backup.exposed",
                        Title = "Potential backup file exposed",
                        Description = candidate.AbsoluteUri,
                        Severity = FindingSeverity.High,
                        Confidence = 0.85,
                    });
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException)
            {
                // A missing candidate is expected and does not fail the aggressive run.
            }
        }

        store.AddRange(findings);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return findings;
    }

    private static IEnumerable<Uri> CreateBackupCandidates(string value)
    {
        var uri = new Uri(value);
        if (uri.AbsolutePath.EndsWith('/')) yield break;
        foreach (var suffix in new[] { "~", ".bak", ".old", ".orig", ".save", ".zip" })
        {
            yield return new UriBuilder(uri) { Path = uri.AbsolutePath + suffix, Query = string.Empty }.Uri;
        }
    }
}
