using System.Net;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Networking;

namespace Morsa.Infrastructure.Acquisition;

/// <summary>Downloads discovered artifacts with scope, redirect and byte-budget validation.</summary>
public sealed class AcquisitionService(
    IMorsaStore store,
    RotatingHttpClient http,
    IProxyPool proxyRuntime,
    IArtifactStorage storage,
    NetworkScopeValidator scopeValidator,
    MorsaConfiguration configuration)
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (maximumRedirects is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(maximumRedirects));
        var current = new Uri(resource.Url, UriKind.Absolute);
        Guid? retainedLeaseId = null;
        try
        {
            for (var redirect = 0; redirect <= maximumRedirects; redirect++)
            {
                var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
                    projectId, current, ActivityMode.Passive, allowPrivateNetworks, cancellationToken).ConfigureAwait(false);
                if (validatedAddresses is null)
                {
                    resource.Status = "scope_rejected";
                    resource.LastError = "URL is outside authorized scope or resolves to a blocked address class.";
                    await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    throw new UnauthorizedAccessException(resource.LastError);
                }

                var result = await http.FetchPinnedAsync(
                    current,
                    proxyPool,
                    new NetworkRequestContext(runId, null, $"fetch:{resource.Id}", current, "acquisition", resource.ProviderId),
                    maximumBytes,
                    validatedAddresses,
                    cancellationToken).ConfigureAwait(false);
                retainedLeaseId = result.ProxyLeaseId ?? retainedLeaseId;
                if ((int)result.StatusCode is >= 300 and < 400 && TryGetLocation(result, current, out var next))
                {
                    // Preserve transport security across redirects; an HTTPS origin cannot silently downgrade acquisition to HTTP.
                    if (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    {
                        resource.Status = "failed";
                        resource.LastError = "HTTPS redirect downgrade was rejected.";
                        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        throw new HttpRequestException(resource.LastError);
                    }

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
        finally
        {
            if (retainedLeaseId is { } leaseId)
            {
                // The lease remains sticky across redirects but must not consume capacity after this resource finishes.
                await proxyRuntime.ReleaseAsync(leaseId, CancellationToken.None).ConfigureAwait(false);
            }
        }
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
                await FetchAsync(
                        projectId,
                        runId,
                        resource,
                        proxyPool,
                        maximumBytes,
                        configuration.Network.MaxRedirects,
                        configuration.Security.AllowPrivateNetworks,
                        cancellationToken)
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

    /// <summary>Moves retryable download failures back to pending without reopening scope-rejected resources.</summary>
    public async Task<int> RequeueFailedAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var failed = await store.DiscoveredResources
            .Where(item => item.ProjectId == projectId && item.Status == "failed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var resource in failed)
        {
            resource.Status = "pending";
            resource.LastError = null;
        }

        if (failed.Count > 0)
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return failed.Count;
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
