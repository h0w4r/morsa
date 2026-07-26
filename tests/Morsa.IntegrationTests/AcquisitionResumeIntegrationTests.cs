using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Domain.Networking;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Configuration;

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

    [Fact]
    public async Task FetchAsync_SequentialResources_ReleasesSuccessfulLeaseAfterEachResource()
    {
        var configuration = new MorsaConfiguration
        {
            Security = new SecurityConfiguration { AllowPrivateNetworks = true },
            Network = new NetworkConfiguration { RequestsPerSecond = 1_000, TimeoutSeconds = 5 },
        };
        var services = new ServiceCollection().AddMorsaCore(_root, configuration);
        services.AddSingleton<INetworkTransportFactory, SuccessfulTransportFactory>();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "lease-release", RootPath = _root };
        store.Add(project);
        store.Add(new ScopeEntry
        {
            ProjectId = project.Id,
            Kind = "url",
            Value = "http://127.0.0.1:12345",
            MaximumMode = ActivityMode.Active,
        });
        var pool = new ProxyPool
        {
            Name = "capacity-one",
            SelectionPolicy = ProxySelectionPolicy.Failover,
            MaxAttempts = 1,
            MaxRotations = 1,
            LeaseTtlSeconds = 900,
            AllowDirectFallback = false,
        };
        store.Add(pool);
        store.Add(new ProxyEndpoint
        {
            PoolId = pool.Id,
            Uri = "http://proxy.invalid:8080/",
            Protocol = ProxyProtocol.Http,
            MaxConcurrency = 1,
        });
        var first = Resource(project.Id, "http://127.0.0.1:12345/first.docx", "pending", null);
        var second = Resource(project.Id, "http://127.0.0.1:12345/second.docx", "pending", null);
        store.Add(first);
        store.Add(second);
        await store.SaveChangesAsync();
        var acquisition = provider.GetRequiredService<AcquisitionService>();
        var runId = Guid.NewGuid();

        _ = await acquisition.FetchAsync(project.Id, runId, first, pool.Name, 4096, 2, true, CancellationToken.None);
        _ = await acquisition.FetchAsync(project.Id, runId, second, pool.Name, 4096, 2, true, CancellationToken.None);

        Assert.Equal(2, store.Artifacts.Count());
        Assert.Equal(2, store.ProxyLeases.Count());
        Assert.All(store.ProxyLeases, lease => Assert.NotNull(lease.ReleasedAt));
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

    private sealed class SuccessfulTransportFactory : INetworkTransportFactory
    {
        public HttpMessageHandler CreateHttpHandler(ProxyEndpoint? endpoint) => new SuccessfulHandler();

        public Task<Stream> ConnectTcpAsync(
            ProxyEndpoint? endpoint,
            string host,
            int port,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The acquisition fixture exercises only HTTP transport.");

        private sealed class SuccessfulHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent("fixture"u8.ToArray()),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                return Task.FromResult(response);
            }
        }
    }
}
