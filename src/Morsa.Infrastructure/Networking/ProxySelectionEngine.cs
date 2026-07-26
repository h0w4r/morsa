using System.Security.Cryptography;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>Stateless implementations of every documented proxy selection policy.</summary>
public sealed class ProxySelectionEngine : IProxySelectionPolicy
{
    public ProxyEndpoint? Select(
        ProxySelectionPolicy policy,
        IReadOnlyList<ProxyEndpoint> eligible,
        string sessionKey,
        long sequence)
    {
        if (eligible.Count == 0)
        {
            return null;
        }

        return policy switch
        {
            ProxySelectionPolicy.Sticky => eligible[StableIndex(sessionKey, eligible.Count)],
            ProxySelectionPolicy.RoundRobin => eligible[(int)(Math.Abs(sequence) % eligible.Count)],
            ProxySelectionPolicy.Random => eligible[RandomNumberGenerator.GetInt32(eligible.Count)],
            ProxySelectionPolicy.Weighted => SelectWeighted(eligible),
            ProxySelectionPolicy.LeastLatency => eligible
                .OrderBy(endpoint => endpoint.EwmaLatencyMs ?? double.MaxValue)
                .ThenBy(endpoint => endpoint.FailureCount)
                .First(),
            ProxySelectionPolicy.Failover => eligible
                .OrderBy(endpoint => endpoint.FailureCount)
                .ThenBy(endpoint => endpoint.EwmaLatencyMs ?? double.MaxValue)
                .First(),
            _ => eligible[0],
        };
    }

    private static int StableIndex(string value, int count)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return (int)(BitConverter.ToUInt32(bytes, 0) % count);
    }

    private static ProxyEndpoint SelectWeighted(IReadOnlyList<ProxyEndpoint> endpoints)
    {
        var totalWeight = endpoints.Sum(endpoint => Math.Max(1, endpoint.Weight));
        var selected = RandomNumberGenerator.GetInt32(totalWeight);
        foreach (var endpoint in endpoints)
        {
            selected -= Math.Max(1, endpoint.Weight);
            if (selected < 0)
            {
                return endpoint;
            }
        }

        return endpoints[^1];
    }
}

