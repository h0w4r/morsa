using System.Net;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;

namespace Morsa.Application.Services;

/// <summary>Enforces project scope and blocks common SSRF address classes.</summary>
public sealed class ScopePolicy
{
    public bool IsUriAllowed(
        Uri uri,
        ActivityMode requestedMode,
        IEnumerable<ScopeEntry> scope,
        bool allowPrivateNetworks)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var address) && !allowPrivateNetworks && IsPrivate(address))
        {
            return false;
        }

        return scope.Any(entry =>
            requestedMode <= entry.MaximumMode &&
            Matches(entry, host));
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }

        return address.Equals(IPAddress.IPv6Loopback) ||
               address.IsIPv6Multicast ||
               address.GetAddressBytes()[0] is 0xfc or 0xfd;
    }

    private static bool Matches(ScopeEntry entry, string host)
    {
        var value = entry.Value.Trim().TrimEnd('.').ToLowerInvariant();
        return entry.Kind switch
        {
            "domain" => host == value || host.EndsWith($".{value}", StringComparison.Ordinal),
            "host" => host == value,
            "url" => Uri.TryCreate(value, UriKind.Absolute, out var allowed) &&
                     string.Equals(allowed.IdnHost, host, StringComparison.OrdinalIgnoreCase),
            "ip" => host == value,
            _ => false,
        };
    }
}

