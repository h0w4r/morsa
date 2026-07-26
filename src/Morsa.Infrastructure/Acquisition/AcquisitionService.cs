using System.Net;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Infrastructure.Networking;

namespace Morsa.Infrastructure.Acquisition;

/// <summary>Downloads discovered artifacts with scope, redirect and byte-budget validation.</summary>
public sealed class AcquisitionService(
    IMorsaStore store,
    RotatingHttpClient http,
    IArtifactStorage storage,
    ScopePolicy scopePolicy)
{
    public async Task<Artifact> FetchAsync(
        Guid projectId,
        Guid runId,
        DiscoveredResource resource,
        string? proxyPool,
        int maximumBytes,
        int maximumRedirects,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
    {
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = new Uri(resource.Url, UriKind.Absolute);
        for (var redirect = 0; redirect <= maximumRedirects; redirect++)
        {
            if (!scopePolicy.IsUriAllowed(current, ActivityMode.Passive, scope, allowPrivateNetworks))
            {
                resource.Status = "scope_rejected";
                resource.LastError = "URL is outside authorized scope or resolves to a blocked address class.";
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                throw new UnauthorizedAccessException(resource.LastError);
            }

            var result = await http.FetchAsync(
                current,
                proxyPool,
                new NetworkRequestContext(runId, null, $"fetch:{resource.Id}", current, "acquisition", resource.ProviderId),
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
            if ((int)result.StatusCode is >= 300 and < 400 && TryGetLocation(result, current, out var next))
            {
                current = next;
                continue;
            }

            if ((int)result.StatusCode is < 200 or >= 300)
            {
                resource.Status = "failed";
                resource.LastError = $"HTTP {(int)result.StatusCode}";
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(resource.LastError);
            }

            await using var stream = new MemoryStream(result.Content, writable: false);
            var stored = await storage.StoreAsync(stream, Path.GetFileName(current.LocalPath), maximumBytes, cancellationToken)
                .ConfigureAwait(false);
            var artifact = new Artifact
            {
                RunId = runId,
                SourceUri = current.AbsoluteUri,
                StoredPath = stored.Path,
                Sha256 = stored.Sha256,
                Size = stored.Size,
                Kind = stored.Kind,
                MimeType = stored.MimeType ?? result.ContentType,
            };
            store.Add(artifact);
            resource.Status = "downloaded";
            resource.Url = current.AbsoluteUri;
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return artifact;
        }

        resource.Status = "failed";
        resource.LastError = "Redirect budget exhausted.";
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(resource.LastError);
    }

    public async Task<(int Downloaded, int Failed)> FetchPendingAsync(
        Guid projectId,
        Guid runId,
        string? proxyPool,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var pending = await store.DiscoveredResources
            .Where(item => item.ProjectId == projectId && item.Status == "pending")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var downloaded = 0;
        var failed = 0;
        foreach (var resource in pending)
        {
            try
            {
                await FetchAsync(projectId, runId, resource, proxyPool, maximumBytes, 5, false, cancellationToken)
                    .ConfigureAwait(false);
                downloaded++;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                resource.Status = "failed";
                resource.LastError = exception.Message;
                failed++;
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (downloaded, failed);
    }

    private static bool TryGetLocation(HttpFetchResult result, Uri current, out Uri next)
    {
        next = current;
        if (!result.Headers.TryGetValue("Location", out var values) || values.Length == 0)
        {
            return false;
        }

        return Uri.TryCreate(current, values[0], out next!);
    }
}

