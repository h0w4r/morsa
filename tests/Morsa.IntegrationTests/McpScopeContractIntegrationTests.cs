using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure;
using Morsa.Mcp.Tools;

namespace Morsa.IntegrationTests;

public sealed class McpScopeContractIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-mcp-scope", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ScopeAdd_CidrInput_NormalizesNetworkAndMatchesCliContract()
    {
        _ = await ProjectScopeTools.ProjectInit(_root, "mcp-cidr", CancellationToken.None);

        _ = await ProjectScopeTools.ScopeAdd(
            _root,
            "192.0.2.129/24",
            kind: null,
            maximum_mode: "active",
            cancellationToken: CancellationToken.None);

        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var entry = Assert.Single(provider.GetRequiredService<IMorsaStore>().ScopeEntries);
        Assert.Equal("cidr", entry.Kind);
        Assert.Equal("192.0.2.0/24", entry.Value);
    }
}
