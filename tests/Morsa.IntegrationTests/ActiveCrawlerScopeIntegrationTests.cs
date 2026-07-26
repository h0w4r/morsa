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
using Morsa.Infrastructure.Discovery;

namespace Morsa.IntegrationTests;

public sealed class ActiveCrawlerScopeIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-active-crawl-scope", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DiscoverAsync_DirectCrawlerOutsideActiveScope_FailsProviderBeforeNetwork()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "crawler-scope", RootPath = _root };
        store.Add(project);
        await store.SaveChangesAsync();
        var runId = Guid.NewGuid();
        var query = new SearchQuery("example.test", ["pdf"], MaxResults: 10);
        var context = new SearchExecutionContext(runId, null, "crawler", null, 10, project.Id);

        var result = await provider.GetRequiredService<DiscoveryService>()
            .DiscoverAsync(project.Id, runId, query, context, ["direct-crawler"], CancellationToken.None);

        Assert.Equal(["direct-crawler"], result.FailedProviders);
        Assert.Equal(0, result.Added);
        Assert.Empty(store.NetworkAttempts);
        var request = Assert.Single(store.ProviderRequests);
        Assert.Equal("failed", request.Status);
        Assert.Contains("outside authorized active scope", request.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_ExplicitPrivateHttpScope_CrawlsWhenConfigurationAllowsPrivateNetworks()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var target = $"http://127.0.0.1:{port}";
        var server = ServeCrawlerResponsesAsync(listener, target);
        var configuration = new MorsaConfiguration
        {
            Security = new SecurityConfiguration { AllowPrivateNetworks = true },
            Network = new NetworkConfiguration { RequestsPerSecond = 1_000, TimeoutSeconds = 5 },
        };

        await using var provider = new ServiceCollection().AddMorsaCore(_root, configuration).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "crawler-private", RootPath = _root };
        store.Add(project);
        store.Add(new ScopeEntry { ProjectId = project.Id, Kind = "url", Value = target, MaximumMode = ActivityMode.Active });
        await store.SaveChangesAsync();
        var runId = Guid.NewGuid();

        var result = await provider.GetRequiredService<DiscoveryService>().DiscoverAsync(
            project.Id,
            runId,
            new SearchQuery(target, ["pdf"], MaxResults: 10),
            new SearchExecutionContext(runId, null, "crawler-private", null, 10, project.Id),
            ["direct-crawler"],
            CancellationToken.None);

        Assert.Empty(result.FailedProviders);
        Assert.Equal(2, result.Added);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, store.NetworkAttempts.Count());
        Assert.Equal(2, store.DiscoveredResources.Count());
    }

    private static async Task ServeCrawlerResponsesAsync(TcpListener listener, string target)
    {
        for (var request = 0; request < 2; request++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? throw new InvalidDataException("Crawler request line is missing.");
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
                // Consume bounded HTTP headers before returning the deterministic fixture.
            }

            var body = requestLine.Contains("/sitemap.xml", StringComparison.Ordinal)
                ? $"<urlset><url><loc>{target}/archive.pdf</loc></url></urlset>"
                : "<html><body><a href=\"/report.pdf\">Report</a></body></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(bytes);
        }
    }
}
