using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Networking;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

[Collection("ConsoleIsolation")]
public sealed class ProxyCliSafetyIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-proxy-cli", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "proxy-cli", RootPath = _root };
        store.Add(project);
        var pool = new ProxyPool { Name = "real" };
        store.Add(pool);
        store.Add(new ProxyPool { Name = "empty", AllowDirectFallback = false });
        await store.SaveChangesAsync();
        store.Add(new ProxyEndpoint
        {
            PoolId = pool.Id,
            Uri = "http://127.0.0.1:8080",
            Protocol = ProxyProtocol.Http,
            DnsMode = ProxyDnsMode.Local,
            Status = ProxyStatus.Degraded,
            ConsecutiveFailures = 3,
        });
        store.Add(new ScopeEntry
        {
            ProjectId = project.Id,
            Kind = "domain",
            Value = "example.test",
            MaximumMode = ActivityMode.Aggressive,
        });
        await store.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProxyReset_UnknownPool_DoesNotResetEveryEndpoint()
    {
        var result = await RunCliAsync("proxy", "reset", "--project", _root, "--pool", "typo", "--json");

        Assert.NotEqual(0, result.ExitCode);
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var endpoint = Assert.Single(provider.GetRequiredService<IMorsaStore>().ProxyEndpoints);
        Assert.Equal(ProxyStatus.Degraded, endpoint.Status);
        Assert.Equal(3, endpoint.ConsecutiveFailures);
    }

    [Fact]
    public async Task ProxyTest_PrivateOutOfScopeTarget_StopsBeforeNetworkAttempt()
    {
        var result = await RunCliAsync(
            "proxy", "test", "real", "--project", _root,
            "--url", "http://169.254.169.254/latest/meta-data/", "--json");

        Assert.Equal(3, result.ExitCode);
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        Assert.Empty(provider.GetRequiredService<IMorsaStore>().NetworkAttempts);
    }

    [Fact]
    public async Task ReconAxfr_PrivateExplicitServer_IsScopeRejectedAndRunIsClosed()
    {
        var result = await RunCliAsync(
            "recon", "axfr", "example.test", "--project", _root,
            "--server", "169.254.169.254", "--json");

        Assert.Equal(3, result.ExitCode);
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var run = Assert.Single(provider.GetRequiredService<IMorsaStore>().Runs.Where(item => item.Command == "recon axfr"));
        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal("scope_rejected", run.CoverageStatus);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task FingerprintTls_EmptyMandatoryPool_DoesNotFallBackDirectAndClosesRun()
    {
        var result = await RunCliAsync(
            "fingerprint", "tls", "example.test", "--port", "443", "--project", _root,
            "--proxy-pool", "empty", "--json");

        Assert.NotEqual(0, result.ExitCode);
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var run = Assert.Single(provider.GetRequiredService<IMorsaStore>().Runs.Where(item => item.Command == "fingerprint tls"));
        Assert.Equal(ExecutionStatus.Failed, run.Status);
        Assert.Equal("failed", run.CoverageStatus);
        Assert.Empty(provider.GetRequiredService<IMorsaStore>().NetworkAttempts);
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            return new CliResult(await Morsa.Cli.Program.Main(arguments), output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
