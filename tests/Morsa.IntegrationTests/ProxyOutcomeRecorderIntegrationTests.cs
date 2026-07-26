using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

public sealed class ProxyOutcomeRecorderIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-proxy-audit", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecordAsync_DestinationWithCredentialsAndQuery_PersistsOnlyRedactedAuthorityAndPath()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var context = new NetworkRequestContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-session",
            new Uri("https://user:secret@example.test:8443/private/report.pdf?token=top-secret#fragment"),
            "test",
            "fixture");
        var outcome = new ProxyOutcome(NetworkOutcome.Success, TimeSpan.FromMilliseconds(25), 200, 128, null, null, null);

        await provider.GetRequiredService<IProxyOutcomeRecorder>().RecordAsync(context, null, outcome, CancellationToken.None);

        var attempt = Assert.Single(provider.GetRequiredService<IMorsaStore>().NetworkAttempts);
        Assert.Equal("https://example.test:8443/private/report.pdf", attempt.Destination);
        Assert.DoesNotContain("secret", attempt.Destination, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", attempt.Destination, StringComparison.OrdinalIgnoreCase);
    }
}
