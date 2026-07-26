using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;

namespace Morsa.IntegrationTests;

public sealed class Socks5HostTransportIntegrationTests
{
    [Fact]
    public async Task ConnectTcpAsync_Socks5Host_EncodesHostnameForRemoteDnsResolution()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var observedHost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOneSocks5RequestAsync(listener, observedHost);
        var endpoint = new ProxyEndpoint
        {
            PoolId = Guid.NewGuid(),
            Uri = $"socks5h://127.0.0.1:{proxyPort}",
            Protocol = ProxyProtocol.Socks5Host,
            DnsMode = ProxyDnsMode.Remote,
        };
        var factory = new NetworkTransportFactory(new NullSecretResolver());

        await using var tunnel = await factory.ConnectTcpAsync(endpoint, "unresolvable.invalid", 443, CancellationToken.None);

        Assert.Equal("unresolvable.invalid", await observedHost.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ServeOneSocks5RequestAsync(TcpListener listener, TaskCompletionSource<string> observedHost)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var greeting = new byte[3];
        await stream.ReadExactlyAsync(greeting);
        Assert.Equal(new byte[] { 5, 1, 0 }, greeting);
        await stream.WriteAsync(new byte[] { 5, 0 });

        var head = new byte[5];
        await stream.ReadExactlyAsync(head);
        Assert.Equal((byte)5, head[0]);
        Assert.Equal((byte)1, head[1]);
        Assert.Equal((byte)3, head[3]);
        var hostBytes = new byte[head[4]];
        await stream.ReadExactlyAsync(hostBytes);
        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes);
        Assert.Equal(443, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
        observedHost.TrySetResult(System.Text.Encoding.ASCII.GetString(hostBytes));

        // Successful IPv4 bind reply; no real destination connection is made by the fake proxy.
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
    }

    private sealed class NullSecretResolver : ISecretResolver
    {
        public NetworkCredential? ResolveNetworkCredential(string? secretRef) => null;
    }
}
