using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>Resolves standard Unix proxy variables while honoring NO_PROXY host suffixes.</summary>
public sealed class EnvironmentProxyResolver
{
    public ProxyEndpoint? Resolve(Uri destination)
    {
        if (IsBypassed(destination)) return null;
        var value = destination.Scheme == "https"
            ? Get("HTTPS_PROXY") ?? Get("ALL_PROXY") ?? Get("HTTP_PROXY")
            : Get("HTTP_PROXY") ?? Get("ALL_PROXY");
        if (string.IsNullOrWhiteSpace(value)) return null;

        var candidate = FileProxySource.Parse(value, null, 1, ["environment"]);
        return new ProxyEndpoint
        {
            Uri = candidate.Uri.ToString(),
            Protocol = candidate.Protocol,
            DnsMode = candidate.DnsMode,
            SecretRef = candidate.SecretRef,
            TagsJson = "[\"environment\"]",
        };
    }

    private static bool IsBypassed(Uri destination)
    {
        var noProxy = Get("NO_PROXY");
        if (string.IsNullOrWhiteSpace(noProxy)) return false;
        var host = destination.IdnHost.TrimEnd('.');
        return noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(pattern => pattern == "*" || HostMatches(host, pattern));
    }

    private static bool HostMatches(string host, string pattern)
    {
        var withoutPort = pattern.Trim().TrimStart('.');
        if (withoutPort.StartsWith('[') && withoutPort.Contains(']'))
        {
            withoutPort = withoutPort[1..withoutPort.IndexOf(']')];
        }
        else if (withoutPort.Count(character => character == ':') == 1)
        {
            withoutPort = withoutPort[..withoutPort.LastIndexOf(':')];
        }

        return host.Equals(withoutPort, StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith('.' + withoutPort, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());
}
