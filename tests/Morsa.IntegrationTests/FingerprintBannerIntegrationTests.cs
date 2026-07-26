using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Recon;

namespace Morsa.IntegrationTests;

public sealed class FingerprintBannerIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-banner", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GrabBannerAsync_HttpProtocol_SendsBoundedHeadRequestAndPersistsResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeHttpBannerAsync(listener, requestLine);
        var configuration = new MorsaConfiguration
        {
            Security = new SecurityConfiguration { AllowPrivateNetworks = true },
            Network = new NetworkConfiguration { RequestsPerSecond = 1_000, TimeoutSeconds = 5 },
        };
        await using var provider = new ServiceCollection().AddMorsaCore(_root, configuration).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "banner", RootPath = _root };
        store.Add(project);
        store.Add(new ScopeEntry { ProjectId = project.Id, Kind = "ip", Value = "127.0.0.1", MaximumMode = ActivityMode.Active });
        await store.SaveChangesAsync();

        var observation = await provider.GetRequiredService<FingerprintService>().GrabBannerAsync(
            project.Id,
            Guid.NewGuid(),
            "127.0.0.1",
            port,
            "http",
            null,
            CancellationToken.None);

        Assert.StartsWith("HEAD / HTTP/1.0", await requestLine.Task.WaitAsync(TimeSpan.FromSeconds(2)), StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200 OK", observation.Banner, StringComparison.Ordinal);
        await server.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(store.ServiceObservations);
    }

    private static async Task ServeHttpBannerAsync(TcpListener listener, TaskCompletionSource<string> requestLine)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        requestLine.SetResult(await reader.ReadLineAsync() ?? string.Empty);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // Consume the complete request before returning a deterministic raw banner.
        }

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nServer: MorsaBannerFixture/1.0\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
    }
}
