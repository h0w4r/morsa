using System.Net;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;

namespace Morsa.Application.Services;

/// <summary>Workspace security defaults applied in addition to explicit per-call allowances.</summary>
public sealed record ScopePolicyOptions(bool AllowPrivateNetworks = false);

/// <summary>Enforces project scope and blocks common SSRF address classes.</summary>
public sealed class ScopePolicy(ScopePolicyOptions? options = null)
{
    public bool IsUriAllowed(
        Uri uri,
        ActivityMode requestedMode,
        IEnumerable<ScopeEntry> scope,
        bool allowPrivateNetworks)
    {
        allowPrivateNetworks |= options?.AllowPrivateNetworks == true;
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
            Matches(entry, uri, host));
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivate(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
                   (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                   (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                   bytes[0] >= 224;
        }

        var ipv6 = address.GetAddressBytes();
        return ipv6[0] is 0xfc or 0xfd ||
               (ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8);
    }

    private static bool Matches(ScopeEntry entry, Uri uri, string host)
    {
        var value = entry.Value.Trim().TrimEnd('.').ToLowerInvariant();
        return entry.Kind switch
        {
            "domain" => host == value || host.EndsWith($".{value}", StringComparison.Ordinal),
            "host" => host == value,
            "url" => Uri.TryCreate(value, UriKind.Absolute, out var allowed) && MatchesUrlScope(allowed, uri, host),
            "ip" => host == value,
            "cidr" => IPAddress.TryParse(host, out var address) && IsInCidr(address, value),
            _ => false,
        };
    }

    private static bool MatchesUrlScope(Uri allowed, Uri requested, string normalizedRequestedHost)
    {
        if (!string.Equals(allowed.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(allowed.IdnHost.TrimEnd('.'), normalizedRequestedHost, StringComparison.OrdinalIgnoreCase) ||
            allowed.Port != requested.Port)
        {
            return false;
        }

        // Uri canonicalizes dot segments before this comparison. A URL scope authorizes its path and descendants,
        // but never a sibling that merely shares the same textual prefix (for example /files-evil).
        var allowedPath = allowed.AbsolutePath.TrimEnd('/');
        if (allowedPath.Length == 0) allowedPath = "/";
        var requestedPath = requested.AbsolutePath;
        return allowedPath == "/" ||
               string.Equals(requestedPath, allowedPath, StringComparison.Ordinal) ||
               requestedPath.StartsWith($"{allowedPath}/", StringComparison.Ordinal);
    }

    private static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix)) return false;
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length || prefix < 0 || prefix > addressBytes.Length * 8) return false;
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = 1 << (7 - bit % 8);
            if ((addressBytes[bit / 8] & mask) != (networkBytes[bit / 8] & mask)) return false;
        }
        return true;
    }
}
