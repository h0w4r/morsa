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
public sealed class DnsReconService(IMorsaStore store)
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
    INetworkTransportFactory transports)
{
    public async Task<ServiceObservation> FingerprintHttpAsync(
        Guid runId,
        Uri uri,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var result = await http.FetchAsync(
            uri,
            proxyPool,
            new NetworkRequestContext(runId, null, $"fingerprint:{uri.Host}", uri, "fingerprint-http", null),
            512 * 1024,
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
        Guid runId,
        string host,
        int port,
        string protocol,
        ProxyEndpoint? endpoint,
        CancellationToken cancellationToken)
    {
        await using var stream = await transports.ConnectTcpAsync(endpoint, host, port, cancellationToken).ConfigureAwait(false);
        if (protocol.Equals("smtp", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"EHLO morsa.local\r\n"), cancellationToken).ConfigureAwait(false);
        }

        var buffer = new byte[8 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
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
        Guid runId,
        string host,
        int port,
        ProxyEndpoint? endpoint,
        CancellationToken cancellationToken)
    {
        await using var transport = await transports.ConnectTcpAsync(endpoint, host, port, cancellationToken).ConfigureAwait(false);
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
