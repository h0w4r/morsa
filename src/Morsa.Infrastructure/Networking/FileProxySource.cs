using System.Globalization;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>Loads user-managed proxy lists from text, CSV, JSON or NDJSON.</summary>
public sealed class FileProxySource(string path) : IProxySource
{
    public string Id => $"file:{Path.GetFileName(path)}";

    public async IAsyncEnumerable<ProxyCandidate> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json")
        {
            await using var stream = File.OpenRead(path);
            var items = await JsonSerializer.DeserializeAsync<List<ProxySourceRecord>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? [];
            foreach (var item in items)
            {
                yield return Parse(item.Uri, item.SecretRef, item.Weight, item.Tags ?? []);
            }

            yield break;
        }

        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var value = line.Trim();
            if (string.IsNullOrEmpty(value) || value.StartsWith('#'))
            {
                continue;
            }

            if (extension is ".jsonl" or ".ndjson")
            {
                var item = JsonSerializer.Deserialize<ProxySourceRecord>(value) ??
                           throw new InvalidDataException("Invalid proxy JSONL record.");
                yield return Parse(item.Uri, item.SecretRef, item.Weight, item.Tags ?? []);
                continue;
            }

            var fields = extension == ".csv" ? value.Split(',') : [value];
            var uri = fields[0].Trim();
            var secretRef = fields.Length > 1 ? fields[1].Trim() : null;
            var weight = fields.Length > 2 && int.TryParse(fields[2], CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 1;
            yield return Parse(uri, secretRef, weight, []);
        }
    }

    public static ProxyCandidate Parse(string uriText, string? secretRef, int weight, IReadOnlyCollection<string> tags)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException($"Invalid proxy URI: {uriText}");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Inline proxy credentials are forbidden; use secret_ref.");
        }

        var (protocol, dnsMode) = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => (ProxyProtocol.Http, ProxyDnsMode.Local),
            "https" => (ProxyProtocol.HttpsConnect, ProxyDnsMode.Local),
            "socks4" => (ProxyProtocol.Socks4, ProxyDnsMode.Local),
            "socks5" => (ProxyProtocol.Socks5, ProxyDnsMode.Local),
            "socks5h" => (ProxyProtocol.Socks5Host, ProxyDnsMode.Remote),
            _ => throw new InvalidDataException($"Unsupported proxy scheme: {uri.Scheme}"),
        };
        return new ProxyCandidate(uri, protocol, dnsMode, secretRef, Math.Max(1, weight), tags);
    }

    private sealed record ProxySourceRecord(
        string Uri,
        string? SecretRef,
        int Weight = 1,
        IReadOnlyCollection<string>? Tags = null);
}

