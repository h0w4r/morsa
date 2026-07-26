using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Discovery;
using Morsa.Infrastructure.Malware;
using Morsa.Infrastructure.Metadata;
using Morsa.Infrastructure.Networking;
using Morsa.Infrastructure.Persistence;
using Morsa.Infrastructure.Pipelines;
using Morsa.Infrastructure.Plugins;
using Morsa.Infrastructure.Recon;
using Morsa.Infrastructure.Reporting;
using Morsa.Infrastructure.Time;
using Morsa.Infrastructure.Web;
using Morsa.Infrastructure.Workspace;

namespace Morsa.Infrastructure;

/// <summary>Registers infrastructure adapters while preserving inward dependencies.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMorsaCore(
        this IServiceCollection services,
        string workspacePath,
        MorsaConfiguration? suppliedConfiguration = null)
    {
        var workspace = new WorkspaceContext(workspacePath);
        var configuration = suppliedConfiguration ?? MorsaConfigurationLoader.LoadForWorkspace(workspace.RootPath);
        services.AddSingleton<IWorkspaceContext>(workspace);
        services.AddSingleton(configuration);
        services.AddSingleton<IClock, SystemClock>();

        services.AddDbContext<MorsaDbContext>(options =>
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = workspace.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString();
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IMorsaStore>(provider => provider.GetRequiredService<MorsaDbContext>());
        services.AddScoped<IStoreInitializer, StoreInitializer>();

        services.AddScoped<RunCoordinator>();
        services.AddScoped<ArtifactAnalysisService>();
        services.AddScoped<CorrelationService>();
        services.AddSingleton(new ScopePolicyOptions(configuration.Security.AllowPrivateNetworks));
        services.AddSingleton<ScopePolicy>();
        services.AddSingleton<IArtifactStorage, ContentAddressableArtifactStorage>();
        services.AddSingleton<IArtifactInspector, MagicByteArtifactInspector>();
        services.AddSingleton<IArtifactExtractorRegistry, ArtifactExtractorRegistry>();
        services.AddSingleton<IArtifactParserGateway, IsolatedArtifactParserGateway>();
        services.AddSingleton<IProxySelectionPolicy, ProxySelectionEngine>();
        services.AddScoped<IProxyPool, PersistentProxyPool>();
        services.AddScoped<IProxyOutcomeRecorder, ProxyOutcomeRecorder>();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddSingleton<INetworkTransportFactory, NetworkTransportFactory>();
        services.AddScoped<RotatingHttpClient>();
        services.AddSingleton<EnvironmentProxyResolver>();
        services.AddSingleton<CompositeProxySource>();
        services.AddScoped<NetworkScopeValidator>();
        services.AddSingleton<TargetRateLimiter>();
        services.AddScoped<AcquisitionService>();
        services.AddScoped<DiscoveryService>();
        services.AddScoped<DiscoveryImportService>();
        services.AddScoped<ISearchProvider, DuckDuckGoSearchProvider>();
        services.AddScoped<ISearchProvider, SearXngSearchProvider>();
        services.AddScoped<ISearchProvider, CommonCrawlSearchProvider>();
        services.AddScoped<ISearchProvider, DirectCrawlerSearchProvider>();
        services.AddScoped<ISearchProvider, LocalIndexSearchProvider>();
        services.AddScoped<DnsReconService>();
        services.AddSingleton<SocksDnsClient>();
        services.AddScoped<FingerprintService>();
        services.AddScoped<MalwareAnalysisService>();
        services.AddScoped<WebMappingService>();
        services.AddScoped<GraphExporter>();
        services.AddScoped<FullPipelineService>();
        services.AddSingleton<SearXngBootstrapService>();
        services.AddSingleton<PluginCatalogService>();
        services.AddScoped<PluginProcessRunner>();

        return services;
    }
}
