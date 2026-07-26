using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Mcp.Tools;

namespace Morsa.IntegrationTests;

[Collection("ConsoleIsolation")]
public sealed class JournalingContractIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-journal-contract", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        store.Add(new MorsaProject { Name = "journal", RootPath = _root });
        await store.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteAsync_OperationThrows_PersistsFailedFinishedRun()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = Assert.Single(store.Projects);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.GetRequiredService<RunCoordinator>().ExecuteAsync<object>(
                project.Id,
                "fixture failure",
                ActivityMode.Passive,
                (_, _) => throw new InvalidDataException("synthetic failure"),
                CancellationToken.None));

        var run = Assert.Single(store.Runs.Where(item => item.Command == "fixture failure"));
        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal("failed", run.CoverageStatus);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task AnalyzeCorrelateAndMalware_CliAndMcp_AllCreateCompletedRuns()
    {
        Assert.Equal(0, await RunCliAsync("analyze", "all", "--project", _root, "--json"));
        Assert.Equal(0, await RunCliAsync("correlate", "--project", _root, "--json"));
        Assert.Equal(0, await RunCliAsync("malware", "scan", "--project", _root, "--json"));
        _ = await ArtifactDiscoveryTools.Analyze(_root, cancellationToken: CancellationToken.None);
        _ = await ArtifactDiscoveryTools.Correlate(_root, CancellationToken.None);
        _ = await ReconMalwareTools.MalwareScan(_root, cancellationToken: CancellationToken.None);

        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var runs = provider.GetRequiredService<IMorsaStore>().Runs
            .Where(item => new[] { "analyze all", "correlate", "malware scan", "mcp analyze", "mcp correlate", "mcp malware scan" }.Contains(item.Command))
            .ToArray();
        Assert.Equal(6, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal(ExecutionStatus.Completed, run.Status);
            Assert.Equal("complete", run.CoverageStatus);
            Assert.NotNull(run.FinishedAt);
        });
    }

    [Fact]
    public async Task McpFetchPending_PartialDownload_PersistsPartialRunInsteadOfSuccess()
    {
        await using (var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider())
        {
            await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
            var store = provider.GetRequiredService<IMorsaStore>();
            var project = Assert.Single(store.Projects);
            store.Add(new DiscoveredResource
            {
                ProjectId = project.Id,
                RunId = Guid.NewGuid(),
                Url = "http://169.254.169.254/latest/meta-data/",
                CanonicalUrl = "http://169.254.169.254/latest/meta-data/",
                ProviderId = "fixture",
                Query = "fixture",
            });
            await store.SaveChangesAsync();
        }

        _ = await ArtifactDiscoveryTools.FetchPending(_root, cancellationToken: CancellationToken.None);

        await using var reopened = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await reopened.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var run = Assert.Single(reopened.GetRequiredService<IMorsaStore>().Runs.Where(item => item.Command == "mcp fetch pending"));
        Assert.Equal(ExecutionStatus.PartiallyFailed, run.Status);
        Assert.Equal("partial_download_failure", run.CoverageStatus);
        Assert.NotNull(run.FinishedAt);
    }

    private static async Task<int> RunCliAsync(params string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return await Morsa.Cli.Program.Main(arguments);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
