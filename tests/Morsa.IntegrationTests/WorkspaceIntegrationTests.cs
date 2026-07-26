using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

public sealed class WorkspaceIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task StoreInitializer_AndArtifactStorage_CreateDurableWorkspace()
    {
        var services = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await using var provider = services;
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();

        var storage = provider.GetRequiredService<IArtifactStorage>();
        await using var content = new MemoryStream("author=tester@example.com"u8.ToArray());
        var artifact = await storage.StoreAsync(content, "sample.txt", 1024, CancellationToken.None);

        Assert.Equal(ArtifactKind.Text, artifact.Kind);
        Assert.Equal(25, artifact.Size);
        Assert.True(File.Exists(artifact.Path));
        Assert.True(File.Exists(Path.Combine(_root, "morsa.db")));
    }
}
