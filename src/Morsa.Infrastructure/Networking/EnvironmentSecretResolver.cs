using System.Net;
using Morsa.Application.Abstractions;

namespace Morsa.Infrastructure.Networking;

/// <summary>Resolves env:NAME credential references without persisting their values.</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public NetworkCredential? ResolveNetworkCredential(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return null;
        }

        if (!secretRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only env: secret references are supported by the built-in resolver.");
        }

        var name = secretRef[4..];
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Required proxy credential environment variable '{name}' is missing.");
        }

        var separator = value.IndexOf(':');
        return separator < 0
            ? new NetworkCredential(value, string.Empty)
            : new NetworkCredential(value[..separator], value[(separator + 1)..]);
    }
}

