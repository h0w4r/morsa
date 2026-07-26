using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

public sealed class PersistenceMigrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-persistence", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeAsync_FreshWorkspace_AppliesInitialMigrationAndWal()
    {
        await using (var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider())
        {
            await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "morsa.db")}");
        await connection.OpenAsync();
        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        // The generated timestamp may change when the initial schema is intentionally regenerated.
        Assert.EndsWith("_InitialCreate", (string?)await migrationCommand.ExecuteScalarAsync(), StringComparison.Ordinal);

        await using var journalCommand = connection.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", ((string?)await journalCommand.ExecuteScalarAsync())?.ToLowerInvariant());
    }

    [Fact]
    public async Task RunCoordinator_AfterProviderRestart_PreservesAndReusesIdempotentTask()
    {
        Guid projectId;
        Guid runId;
        Guid taskId;
        await using (var firstProvider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider())
        {
            await firstProvider.GetRequiredService<IStoreInitializer>().InitializeAsync();
            var store = firstProvider.GetRequiredService<IMorsaStore>();
            var project = new MorsaProject { Name = "restart", RootPath = _root, DefaultMode = ActivityMode.Passive };
            store.Add(project);
            await store.SaveChangesAsync();

            var coordinator = firstProvider.GetRequiredService<RunCoordinator>();
            var run = await coordinator.StartAsync(project.Id, "test restart", ActivityMode.Passive, CancellationToken.None);
            var task = await coordinator.GetOrCreateTaskAsync(run, "test", "stable-key", "{}", CancellationToken.None);
            projectId = project.Id;
            runId = run.Id;
            taskId = task.Id;
        }

        SqliteConnection.ClearAllPools();
        await using var secondProvider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await secondProvider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var reopenedStore = secondProvider.GetRequiredService<IMorsaStore>();
        var reopenedRun = Assert.Single(reopenedStore.Runs.Where(item => item.Id == runId));
        var reused = await secondProvider.GetRequiredService<RunCoordinator>()
            .GetOrCreateTaskAsync(reopenedRun, "ignored-on-reuse", "stable-key", null, CancellationToken.None);

        Assert.Equal(projectId, reopenedRun.ProjectId);
        Assert.Equal(taskId, reused.Id);
        Assert.Single(reopenedStore.Tasks.Where(item => item.RunId == runId && item.IdempotencyKey == "stable-key"));
    }
}
