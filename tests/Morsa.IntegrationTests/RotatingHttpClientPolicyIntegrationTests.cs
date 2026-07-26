using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Networking;

namespace Morsa.IntegrationTests;

public sealed class RotatingHttpClientPolicyIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-rotating-policy", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FetchAsync_NamedPoolMissing_FailsBeforeAnySilentDirectConnection()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var destination = new Uri("http://127.0.0.1:9/should-not-connect");
        var context = new NetworkRequestContext(null, null, "missing-pool", destination, "test", null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<RotatingHttpClient>()
                .FetchAsync(destination, "does-not-exist", context, 1024, CancellationToken.None));

        Assert.Contains("does not exist or is disabled", exception.Message, StringComparison.Ordinal);
        Assert.Empty(provider.GetRequiredService<IMorsaStore>().NetworkAttempts);
    }
}
