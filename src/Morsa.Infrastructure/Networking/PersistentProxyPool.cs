using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>SQLite-backed sticky leases and bounded proxy selection.</summary>
public sealed class PersistentProxyPool(
    IMorsaStore store,
    IProxySelectionPolicy selection,
    IClock clock) : IProxyPool
{
    public async Task<ProxyLease?> AcquireAsync(
        string poolName,
        NetworkRequestContext context,
        IReadOnlySet<Guid>? excludedEndpoints,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var pool = await store.ProxyPools
            .SingleOrDefaultAsync(item => item.Name == poolName && item.Enabled, cancellationToken)
            .ConfigureAwait(false);
        if (pool is null)
        {
            return null;
        }

        var activeLeases = await store.ProxyLeases
            .Where(lease => lease.ReleasedAt == null && lease.ExpiresAt > now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = activeLeases.FirstOrDefault(lease => lease.SessionKey == context.SessionKey);
        if (existing is not null && (excludedEndpoints is null || !excludedEndpoints.Contains(existing.ProxyEndpointId)))
        {
            var existingEndpoint = await store.ProxyEndpoints
                .SingleOrDefaultAsync(endpoint => endpoint.Id == existing.ProxyEndpointId, cancellationToken)
                .ConfigureAwait(false);
            if (existingEndpoint is not null && IsEligible(existingEndpoint, now))
            {
                return existing;
            }
        }

        var endpoints = await store.ProxyEndpoints
            .Where(endpoint => endpoint.PoolId == pool.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var leaseCounts = activeLeases
            .GroupBy(lease => lease.ProxyEndpointId)
            .ToDictionary(group => group.Key, group => group.Count());
        var eligible = endpoints
            .Where(endpoint => IsEligible(endpoint, now))
            .Where(endpoint => excludedEndpoints is null || !excludedEndpoints.Contains(endpoint.Id))
            .Where(endpoint => !leaseCounts.TryGetValue(endpoint.Id, out var count) || count < endpoint.MaxConcurrency)
            .ToArray();

        var endpoint = selection.Select(pool.SelectionPolicy, eligible, context.SessionKey, activeLeases.Count);
        if (endpoint is null)
        {
            return null;
        }

        var lease = new ProxyLease
        {
            RunId = context.RunId,
            TaskId = context.TaskId,
            ProxyEndpointId = endpoint.Id,
            SessionKey = context.SessionKey,
            AcquiredAt = now,
            ExpiresAt = now.AddSeconds(pool.LeaseTtlSeconds),
        };
        store.Add(lease);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    public async Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var lease = await store.ProxyLeases.SingleOrDefaultAsync(item => item.Id == leaseId, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null || lease.ReleasedAt is not null)
        {
            return;
        }

        lease.ReleasedAt = clock.UtcNow;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsEligible(ProxyEndpoint endpoint, DateTimeOffset now) =>
        endpoint.Status is not (ProxyStatus.Disabled or ProxyStatus.Quarantined or ProxyStatus.Unavailable) &&
        (endpoint.CooldownUntil is null || endpoint.CooldownUntil <= now);
}

