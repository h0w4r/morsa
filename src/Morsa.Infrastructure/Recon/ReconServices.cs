using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DnsClient;
using DnsClient.Protocol;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Domain.Recon;
using Morsa.Infrastructure.Networking;

namespace Morsa.Infrastructure.Recon;

/// <summary>Performs bounded DNS queries using a maintained cross-platform client.</summary>
public sealed class DnsReconService(IMorsaStore store, SocksDnsClient socksDns)
{
    private static readonly QueryType[] DefaultTypes =
        [QueryType.A, QueryType.AAAA, QueryType.MX, QueryType.NS, QueryType.SOA, QueryType.TXT, QueryType.CNAME, QueryType.SRV, QueryType.CAA];

    public async Task<IReadOnlyList<DnsObservation>> QueryAsync(
        Guid runId,
        string name,
        IReadOnlyCollection<QueryType>? types,
        CancellationToken cancellationToken)
    {
        var client = new LookupClient(new LookupClientOptions
        {
            Timeout = TimeSpan.FromSeconds(5),
            Retries = 1,
            UseCache = false,
            ContinueOnDnsError = true,
        });
        var observations = new List<DnsObservation>();
        foreach (var type in types ?? DefaultTypes)
        {
            var response = await client.QueryAsync(name, type, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var answer in response.Answers)
            {
                observations.Add(new DnsObservation
                {
                    RunId = runId,
                    Name = answer.DomainName.Value.TrimEnd('.'),
                    RecordType = answer.RecordType.ToString(),
                    Value = NormalizeRecord(answer),
                    Ttl = checked((uint)answer.InitialTimeToLive),
                    Source = "dns",
                });
            }
        }

        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    /// <summary>Queries every requested type through remote SOCKS DNS and persists the answers.</summary>
    public async Task<IReadOnlyList<DnsObservation>> QueryViaProxyAsync(
        Guid runId,
        string name,
        IReadOnlyCollection<QueryType>? types,
        ProxyEndpoint endpoint,
        string resolver,
        CancellationToken cancellationToken)
    {
        var observations = new List<DnsObservation>();
        foreach (var type in types ?? DefaultTypes)
        {
            observations.AddRange(await socksDns.QueryAsync(runId, name, type, endpoint, resolver, cancellationToken).ConfigureAwait(false));
        }
        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    public async Task<IReadOnlyList<DnsObservation>> ReverseAsync(
        Guid runId,
        IEnumerable<System.Net.IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        var client = new LookupClient();
        var observations = new List<DnsObservation>();
        foreach (var address in addresses)
        {
            var response = await client.QueryReverseAsync(address, cancellationToken).ConfigureAwait(false);
            foreach (var ptr in response.Answers.PtrRecords())
            {
                observations.Add(new DnsObservation
                {
                    RunId = runId,
                    Name = address.ToString(),
                    RecordType = "PTR",
                    Value = ptr.PtrDomainName.Value.TrimEnd('.'),
                    Ttl = checked((uint)ptr.InitialTimeToLive),
                    Source = "reverse-dns",
                });
            }
        }

        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    public async Task<IReadOnlyList<DnsObservation>> ReverseViaProxyAsync(
        Guid runId,
        IEnumerable<System.Net.IPAddress> addresses,
        ProxyEndpoint endpoint,
        string resolver,
        CancellationToken cancellationToken)
    {
        var observations = new List<DnsObservation>();
        foreach (var address in addresses)
        {
            var reverseName = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? string.Join('.', address.GetAddressBytes().Reverse()) + ".in-addr.arpa"
                : string.Join('.', address.GetAddressBytes().Reverse().SelectMany(value => new[] { (value & 0x0f).ToString("x"), (value >> 4).ToString("x") })) + ".ip6.arpa";
            observations.AddRange(await socksDns.QueryAsync(runId, reverseName, QueryType.PTR, endpoint, resolver, cancellationToken).ConfigureAwait(false));
        }
        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    /// <summary>Resolves a bounded label dictionary and suppresses wildcard DNS answers.</summary>
    public async Task<IReadOnlyList<DnsObservation>> DiscoverSubdomainsAsync(
        Guid runId,
        string domain,
        IEnumerable<string> labels,
        int budget,
        CancellationToken cancellationToken)
    {
        var client = new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 0, UseCache = false });
        var wildcardName = $"morsa-{Guid.NewGuid():N}.{domain}";
        var wildcard = await ResolveAddressesAsync(client, wildcardName, cancellationToken).ConfigureAwait(false);
        var observations = new List<DnsObservation>();
        foreach (var label in labels.Select(value => value.Trim().ToLowerInvariant()).Where(value => value.Length > 0).Distinct().Take(Math.Clamp(budget, 1, 100_000)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.Text.RegularExpressions.Regex.IsMatch(label, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) continue;
            var name = $"{label}.{domain.TrimEnd('.')}";
            var addresses = await ResolveAddressesAsync(client, name, cancellationToken).ConfigureAwait(false);
            if (addresses.Count == 0 || (wildcard.Count > 0 && addresses.SetEquals(wildcard))) continue;
            observations.AddRange(addresses.Select(address => new DnsObservation
            {
                RunId = runId,
                Name = name,
                RecordType = address.Contains(':', StringComparison.Ordinal) ? "AAAA" : "A",
                Value = address,
                Source = "subdomain-dictionary",
            }));
        }

        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    /// <summary>Attempts an explicit TCP AXFR against a selected authoritative server.</summary>
    public async Task<IReadOnlyList<DnsObservation>> ZoneTransferAsync(
        Guid runId,
        string zone,
        string server,
        CancellationToken cancellationToken)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(server.TrimEnd('.'), cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0) throw new InvalidOperationException($"Name server '{server}' has no address.");
        var client = new LookupClient(new LookupClientOptions
        {
            UseTcpOnly = true,
            Timeout = TimeSpan.FromSeconds(15),
            Retries = 0,
            UseCache = false,
        });
        var response = await client.QueryServerAsync(addresses, zone.TrimEnd('.'), QueryType.AXFR, QueryClass.IN, cancellationToken)
            .ConfigureAwait(false);
        var observations = response.AllRecords.Take(100_000).Select(record => new DnsObservation
        {
            RunId = runId,
            Name = record.DomainName.Value.TrimEnd('.'),
            RecordType = record.RecordType.ToString(),
            Value = NormalizeRecord(record),
            Ttl = checked((uint)record.InitialTimeToLive),
            Source = $"axfr:{server}",
        }).ToArray();
        store.AddRange(observations);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observations;
    }

    private static async Task<HashSet<string>> ResolveAddressesAsync(LookupClient client, string name, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in new[] { QueryType.A, QueryType.AAAA })
        {
            try
            {
                var response = await client.QueryAsync(name, type, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var address in response.Answers.OfType<AddressRecord>()) result.Add(address.Address.ToString());
            }
            catch (DnsResponseException)
            {
                // NXDOMAIN and refused queries are expected during bounded enumeration.
            }
        }
        return result;
    }

    private static string NormalizeRecord(DnsResourceRecord record) => record switch
    {
        AddressRecord address => address.Address.ToString(),
        MxRecord mx => $"{mx.Preference} {mx.Exchange.Value.TrimEnd('.')}",
        NsRecord ns => ns.NSDName.Value.TrimEnd('.'),
        CNameRecord cname => cname.CanonicalName.Value.TrimEnd('.'),
        PtrRecord ptr => ptr.PtrDomainName.Value.TrimEnd('.'),
        _ => record.ToString(),
    };
}

/// <summary>Collects HTTP, raw banner and TLS evidence through direct or SOCKS transports.</summary>
public sealed class FingerprintService(
    IMorsaStore store,
    RotatingHttpClient http,
    INetworkTransportFactory transports,
    NetworkScopeValidator scopeValidator)
{
    public async Task<ServiceObservation> FingerprintHttpAsync(
        Guid projectId,
        Guid runId,
        Uri uri,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
            projectId, uri, Morsa.Domain.Common.ActivityMode.Active, false, cancellationToken).ConfigureAwait(false) ??
            throw new UnauthorizedAccessException("HTTP fingerprint target is outside authorized active scope or resolves to a blocked address class.");
        var result = await http.FetchPinnedAsync(
            uri,
            proxyPool,
            new NetworkRequestContext(runId, null, $"fingerprint:{uri.Host}", uri, "fingerprint-http", null),
            512 * 1024,
            validatedAddresses,
            cancellationToken).ConfigureAwait(false);
        var technology = DetectTechnology(result.Headers, result.Content);
        var observation = new ServiceObservation
        {
            RunId = runId,
            Host = uri.IdnHost,
            Port = uri.Port,
            Protocol = uri.Scheme,
            Banner = string.Join("; ", result.Headers.Select(header => $"{header.Key}={string.Join(',', header.Value)}")),
            Technology = technology,
        };
        store.Add(observation);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observation;
    }

    public async Task<ServiceObservation> GrabBannerAsync(
        Guid projectId,
        Guid runId,
        string host,
        int port,
        string protocol,
        ProxyEndpoint? endpoint,
        CancellationToken cancellationToken)
    {
        var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
            projectId,
            new UriBuilder("https", host, port).Uri,
            Morsa.Domain.Common.ActivityMode.Active,
            false,
            cancellationToken).ConfigureAwait(false) ??
            throw new UnauthorizedAccessException("Banner target is outside authorized active scope or resolves to a blocked address class.");
        var transportHost = endpoint?.DnsMode == ProxyDnsMode.Remote ? host : validatedAddresses[0].ToString();
        await using var stream = await transports.ConnectTcpAsync(endpoint, transportHost, port, cancellationToken).ConfigureAwait(false);
        if (protocol.Equals("smtp", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"EHLO morsa.local\r\n"), cancellationToken).ConfigureAwait(false);
        }
        else if (protocol.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            // HTTP servers speak only after a request; use HEAD to collect headers without downloading a response body.
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n"),
                cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[8 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Service banner read timed out after 5 seconds.", exception);
        }
        var observation = new ServiceObservation
        {
            RunId = runId,
            Host = host,
            Port = port,
            Protocol = protocol,
            Banner = Encoding.UTF8.GetString(buffer, 0, read).Trim(),
        };
        store.Add(observation);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observation;
    }

    public async Task<ServiceObservation> InspectTlsAsync(
        Guid projectId,
        Guid runId,
        string host,
        int port,
        ProxyEndpoint? endpoint,
        CancellationToken cancellationToken)
    {
        var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
            projectId,
            new UriBuilder("https", host, port).Uri,
            Morsa.Domain.Common.ActivityMode.Active,
            false,
            cancellationToken).ConfigureAwait(false) ??
            throw new UnauthorizedAccessException("TLS target is outside authorized active scope or resolves to a blocked address class.");
        var transportHost = endpoint?.DnsMode == ProxyDnsMode.Remote ? host : validatedAddresses[0].ToString();
        await using var transport = await transports.ConnectTcpAsync(endpoint, transportHost, port, cancellationToken).ConfigureAwait(false);
        using var tls = new SslStream(transport, leaveInnerStreamOpen: false, (_, _, _, errors) => errors == SslPolicyErrors.None);
        await tls.AuthenticateAsClientAsync(host).WaitAsync(cancellationToken).ConfigureAwait(false);
        var certificate = tls.RemoteCertificate is null ? null : new X509Certificate2(tls.RemoteCertificate);
        var observation = new ServiceObservation
        {
            RunId = runId,
            Host = host,
            Port = port,
            Protocol = "tls",
            Banner = tls.SslProtocol.ToString(),
            TlsSubject = certificate?.Subject,
            TlsIssuer = certificate?.Issuer,
        };
        store.Add(observation);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return observation;
    }

    private static string? DetectTechnology(IReadOnlyDictionary<string, string[]> headers, byte[] content)
    {
        if (headers.TryGetValue("Server", out var server)) return string.Join(' ', server);
        if (headers.ContainsKey("X-Powered-By")) return string.Join(' ', headers["X-Powered-By"]);
        var html = Encoding.UTF8.GetString(content.AsSpan(0, Math.Min(content.Length, 128 * 1024)));
        if (html.Contains("wp-content", StringComparison.OrdinalIgnoreCase)) return "WordPress";
        if (html.Contains("__next", StringComparison.OrdinalIgnoreCase)) return "Next.js";
        if (html.Contains("drupal", StringComparison.OrdinalIgnoreCase)) return "Drupal";
        return null;
    }
}
