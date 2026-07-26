using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Configuration;

namespace Morsa.Infrastructure.Networking;

/// <summary>Re-resolves every destination so redirects and DNS rebinding cannot bypass project scope.</summary>
public sealed class NetworkScopeValidator(
    IMorsaStore store,
    ScopePolicy scopePolicy,
    MorsaConfiguration configuration)
{
    public async Task<bool> IsAllowedAsync(
        Guid projectId,
        Uri uri,
        ActivityMode mode,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
        => await ResolveAllowedAddressesAsync(projectId, uri, mode, allowPrivateNetworks, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Validates scope and returns the exact DNS answers that the direct HTTP transport must use.
    /// Returning the addresses closes the validation/connect DNS-rebinding race for direct requests.
    /// </summary>
    public async Task<IPAddress[]?> ResolveAllowedAddressesAsync(
        Guid projectId,
        Uri uri,
        ActivityMode mode,
        bool allowPrivateNetworks,
        CancellationToken cancellationToken)
    {
        var effectiveAllowPrivateNetworks = allowPrivateNetworks || configuration.Security.AllowPrivateNetworks;
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == projectId).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(uri.UserInfo) || !scopePolicy.IsUriAllowed(uri, mode, scope, effectiveAllowPrivateNetworks)) return null;
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
            return effectiveAllowPrivateNetworks || !ScopePolicy.IsPrivate(literal) ? [literal] : null;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }

        // Fail closed if any answer can route the request into a protected address class.
        return addresses.Length > 0 && (effectiveAllowPrivateNetworks || addresses.All(address => !ScopePolicy.IsPrivate(address)))
            ? addresses.Distinct().ToArray()
            : null;
    }
}

/// <summary>Global per-target pacing is shared by every proxy identity.</summary>
public sealed class TargetRateLimiter(MorsaConfiguration? configuration = null)
{
    private readonly ConcurrentDictionary<string, TargetGate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _requestsPerSecond = ParseRate(configuration);

    public async Task WaitAsync(Uri destination, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(destination.IdnHost, _ => new TargetGate());
        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var delay = gate.NextAllowed - now;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            gate.NextAllowed = DateTimeOffset.UtcNow.AddSeconds(1d / _requestsPerSecond);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }

    private static double ParseRate(MorsaConfiguration? configuration) =>
        double.TryParse(Environment.GetEnvironmentVariable("MORSA_REQUESTS_PER_SECOND"), System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? Math.Clamp(rate, 0.1, 1000)
            : configuration?.Network.RequestsPerSecond ?? 2;

    private sealed class TargetGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public DateTimeOffset NextAllowed { get; set; } = DateTimeOffset.MinValue;
    }
}
