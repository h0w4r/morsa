using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Networking;

namespace Morsa.IntegrationTests;

public sealed class NetworkScopeValidatorIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-network-scope", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task IsAllowedAsync_ReservedLiteralIp_FailsClosedUnlessPrivateNetworksExplicitlyEnabled()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var projectId = Guid.NewGuid();
        var store = provider.GetRequiredService<IMorsaStore>();
        store.Add(new ScopeEntry { ProjectId = projectId, Kind = "ip", Value = "169.254.169.254", MaximumMode = ActivityMode.Active });
        await store.SaveChangesAsync();
        var validator = provider.GetRequiredService<NetworkScopeValidator>();
        var destination = new Uri("http://169.254.169.254/latest/meta-data/");

        Assert.False(await validator.IsAllowedAsync(projectId, destination, ActivityMode.Active, false, CancellationToken.None));
        Assert.True(await validator.IsAllowedAsync(projectId, destination, ActivityMode.Active, true, CancellationToken.None));
    }
}
