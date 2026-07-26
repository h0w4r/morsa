using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;
using Morsa.Infrastructure.Networking;
using Morsa.Infrastructure.Persistence;
using Morsa.Infrastructure.Time;
using Morsa.Infrastructure.Workspace;

namespace Morsa.Infrastructure;

/// <summary>Registers infrastructure adapters while preserving inward dependencies.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMorsaCore(this IServiceCollection services, string workspacePath)
    {
        var workspace = new WorkspaceContext(workspacePath);
        services.AddSingleton<IWorkspaceContext>(workspace);
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
        services.AddSingleton<ScopePolicy>();
        services.AddSingleton<IArtifactStorage, ContentAddressableArtifactStorage>();
        services.AddSingleton<IArtifactInspector, MagicByteArtifactInspector>();
        services.AddSingleton<IArtifactExtractorRegistry, ArtifactExtractorRegistry>();
        services.AddSingleton<IProxySelectionPolicy, ProxySelectionEngine>();
        services.AddScoped<IProxyPool, PersistentProxyPool>();
        services.AddScoped<IProxyOutcomeRecorder, ProxyOutcomeRecorder>();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddSingleton<INetworkTransportFactory, NetworkTransportFactory>();

        return services;
    }
}
