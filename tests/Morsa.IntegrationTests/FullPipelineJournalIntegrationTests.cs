using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Pipelines;

namespace Morsa.IntegrationTests;

public sealed class FullPipelineJournalIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-pipeline-journal", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_EmptyDeterministicProvider_PersistsEveryCompletedIdempotentStage()
    {
        var services = new ServiceCollection().AddMorsaCore(_root);
        services.AddSingleton<ISearchProvider, EmptySearchProvider>();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "pipeline", RootPath = _root, DefaultMode = ActivityMode.Passive };
        store.Add(project);
        await store.SaveChangesAsync();

        var result = await provider.GetRequiredService<FullPipelineService>().RunAsync(
            project.Id, "example.test", ["pdf"], ["fixture-empty"], null, false, CancellationToken.None);

        Assert.Equal("complete", result.Coverage);
        var run = Assert.Single(store.Runs.Where(item => item.Id == result.RunId));
        Assert.Equal(ExecutionStatus.Completed, run.Status);
        var tasks = store.Tasks.Where(item => item.RunId == result.RunId).ToArray();
        Assert.Equal(4, tasks.Length);
        Assert.All(tasks, task => Assert.Equal(ExecutionStatus.Completed, task.Status));
        Assert.Equal(4, tasks.Select(task => task.IdempotencyKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tasks, task => Assert.Equal(1, task.AttemptCount));
    }

    private sealed class EmptySearchProvider : ISearchProvider
    {
        public string Id => "fixture-empty";

        public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderHealth(true, "fixture"));

        public async IAsyncEnumerable<SearchResult> SearchAsync(
            SearchQuery query,
            SearchExecutionContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
