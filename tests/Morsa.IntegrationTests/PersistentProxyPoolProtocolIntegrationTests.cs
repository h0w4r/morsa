using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

public sealed class PersistentProxyPoolProtocolIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-proxy-protocol", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AcquireAsync_RemoteDnsRequirement_SkipsHttpAndLocalDnsEndpoints()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var pool = new ProxyPool { Name = "dns", SelectionPolicy = ProxySelectionPolicy.Failover };
        store.Add(pool);
        await store.SaveChangesAsync();
        var http = Endpoint(pool.Id, "http://127.0.0.1:8080", ProxyProtocol.Http, ProxyDnsMode.Local);
        var localSocks = Endpoint(pool.Id, "socks5://127.0.0.1:1080", ProxyProtocol.Socks5, ProxyDnsMode.Local);
        var remoteSocks = Endpoint(pool.Id, "socks5h://127.0.0.1:1081", ProxyProtocol.Socks5Host, ProxyDnsMode.Remote);
        store.AddRange([http, localSocks, remoteSocks]);
        await store.SaveChangesAsync();
        var destination = new Uri("https://example.test/");
        var context = new NetworkRequestContext(null, null, "dns", destination, "dns", null, ProxyProtocol.Socks5Host);

        var lease = await provider.GetRequiredService<IProxyPool>().AcquireAsync("dns", context, null, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(remoteSocks.Id, lease.ProxyEndpointId);
    }

    [Fact]
    public async Task AcquireAsync_RequiredProtocolUnavailable_ReturnsNoLease()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var pool = new ProxyPool { Name = "http-only" };
        store.Add(pool);
        await store.SaveChangesAsync();
        store.Add(Endpoint(pool.Id, "http://127.0.0.1:8080", ProxyProtocol.Http, ProxyDnsMode.Local));
        await store.SaveChangesAsync();
        var destination = new Uri("https://example.test/");
        var context = new NetworkRequestContext(null, null, "raw", destination, "banner", null, ProxyProtocol.Socks5);

        Assert.Null(await provider.GetRequiredService<IProxyPool>().AcquireAsync("http-only", context, null, CancellationToken.None));
    }

    private static ProxyEndpoint Endpoint(Guid poolId, string uri, ProxyProtocol protocol, ProxyDnsMode dnsMode) => new()
    {
        PoolId = poolId,
        Uri = uri,
        Protocol = protocol,
        DnsMode = dnsMode,
    };
}
