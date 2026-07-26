using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
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
}
