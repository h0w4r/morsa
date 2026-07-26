using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Discovery;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Acquisition;

namespace Morsa.IntegrationTests;

public sealed class AcquisitionResumeIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-acquisition-resume", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RequeueFailedAsync_FailedResourcesBecomePendingWithoutBroadeningScopeRejections()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "resume", RootPath = _root };
        store.Add(project);
        var failed = Resource(project.Id, "http://example.test/failed.docx", "failed", "connection failed");
        var pending = Resource(project.Id, "http://example.test/pending.docx", "pending", null);
        var rejected = Resource(project.Id, "http://169.254.169.254/metadata", "scope_rejected", "outside scope");
        store.Add(failed);
        store.Add(pending);
        store.Add(rejected);
        await store.SaveChangesAsync();

        var requeued = await provider.GetRequiredService<AcquisitionService>()
            .RequeueFailedAsync(project.Id, CancellationToken.None);

        Assert.Equal(1, requeued);
        Assert.Equal("pending", failed.Status);
        Assert.Null(failed.LastError);
        Assert.Equal("pending", pending.Status);
        Assert.Equal("scope_rejected", rejected.Status);
        Assert.Equal("outside scope", rejected.LastError);
    }

    private static DiscoveredResource Resource(Guid projectId, string url, string status, string? error) => new()
    {
        ProjectId = projectId,
        Url = url,
        CanonicalUrl = url,
        ProviderId = "fixture",
        Query = "resume",
        Status = status,
        LastError = error,
    };
}
