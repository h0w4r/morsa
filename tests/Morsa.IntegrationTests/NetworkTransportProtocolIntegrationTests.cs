using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;

namespace Morsa.IntegrationTests;

/// <summary>Wire-level coverage for raw TCP proxy negotiations used by recon modules.</summary>
public sealed class NetworkTransportProtocolIntegrationTests
{
    [Fact]
    public async Task ConnectTcpAsync_HttpConnect_SendsProxyCredentialOnlyToProxy()
    {
        using var listener = StartListener(out var proxyPort);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var header = await ReadHttpHeaderAsync(stream);
            Assert.StartsWith("CONNECT target.example:443 HTTP/1.1\r\n", header, StringComparison.Ordinal);
            Assert.Contains("Proxy-Authorization: Basic dXNlcjpwYXNz\r\n", header, StringComparison.Ordinal);
            await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray());
            await EchoMarkerAsync(stream);
        });
        var endpoint = CreateEndpoint(proxyPort, ProxyProtocol.Http, ProxyDnsMode.Remote, "fixture");
        var factory = new NetworkTransportFactory(new FixtureSecretResolver());

        await using var tunnel = await factory.ConnectTcpAsync(endpoint, "target.example", 443, CancellationToken.None);
        await AssertEchoAsync(tunnel);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectTcpAsync_Socks4a_EncodesRemoteHostnameAndPort()
    {
        using var listener = StartListener(out var proxyPort);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var prefix = new byte[9];
            await stream.ReadExactlyAsync(prefix);
            Assert.Equal(new byte[] { 4, 1 }, prefix[..2]);
            Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(2, 2)));
            Assert.Equal(new byte[] { 0, 0, 0, 1 }, prefix[4..8]);
            Assert.Equal(0, prefix[8]);
            Assert.Equal("target.example", await ReadNullTerminatedAsciiAsync(stream));
            await stream.WriteAsync(new byte[] { 0, 0x5a, 0, 0, 0, 0, 0, 0 });
            await EchoMarkerAsync(stream);
        });
        var endpoint = CreateEndpoint(proxyPort, ProxyProtocol.Socks4, ProxyDnsMode.Remote);
        var factory = new NetworkTransportFactory(new FixtureSecretResolver());

        await using var tunnel = await factory.ConnectTcpAsync(endpoint, "target.example", 443, CancellationToken.None);
        await AssertEchoAsync(tunnel);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConnectTcpAsync_Socks5Local_EncodesPinnedIpLiteral()
    {
        using var listener = StartListener(out var proxyPort);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting);
            Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
            await stream.WriteAsync(new byte[] { 5, 0 });

            var request = new byte[10];
            await stream.ReadExactlyAsync(request);
            Assert.Equal(new byte[] { 5, 1, 0, 1 }, request[..4]);
            Assert.Equal(IPAddress.Parse("203.0.113.7").GetAddressBytes(), request[4..8]);
            Assert.Equal(8443, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8, 2)));
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
            await EchoMarkerAsync(stream);
        });
        var endpoint = CreateEndpoint(proxyPort, ProxyProtocol.Socks5, ProxyDnsMode.Local);
        var factory = new NetworkTransportFactory(new FixtureSecretResolver());

        await using var tunnel = await factory.ConnectTcpAsync(endpoint, "203.0.113.7", 8443, CancellationToken.None);
        await AssertEchoAsync(tunnel);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TcpListener StartListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static ProxyEndpoint CreateEndpoint(int port, ProxyProtocol protocol, ProxyDnsMode dnsMode, string? secretRef = null) =>
        new()
        {
            PoolId = Guid.NewGuid(),
            Uri = $"http://127.0.0.1:{port}",
            Protocol = protocol,
            DnsMode = dnsMode,
            SecretRef = secretRef,
        };

    private static async Task<string> ReadHttpHeaderAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            await stream.ReadExactlyAsync(single);
            bytes.Add(single[0]);
            if (bytes.Count >= 4 && bytes[^4..].SequenceEqual("\r\n\r\n"u8.ToArray()))
                return Encoding.ASCII.GetString(bytes.ToArray());
        }

        throw new InvalidDataException("Fixture HTTP header exceeded its budget.");
    }

    private static async Task<string> ReadNullTerminatedAsciiAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count < 255)
        {
            await stream.ReadExactlyAsync(single);
            if (single[0] == 0) return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(single[0]);
        }

        throw new InvalidDataException("Fixture SOCKS hostname exceeded its budget.");
    }

    private static async Task EchoMarkerAsync(Stream stream)
    {
        var marker = new byte[1];
        await stream.ReadExactlyAsync(marker);
        await stream.WriteAsync(marker);
    }

    private static async Task AssertEchoAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x5a });
        var response = new byte[1];
        await stream.ReadExactlyAsync(response);
        Assert.Equal(0x5a, response[0]);
    }

    private sealed class FixtureSecretResolver : ISecretResolver
    {
        public NetworkCredential? ResolveNetworkCredential(string? secretRef) =>
            secretRef == "fixture" ? new NetworkCredential("user", "pass") : null;
    }
}
