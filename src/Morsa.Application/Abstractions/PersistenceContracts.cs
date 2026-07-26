using Morsa.Domain.Artifacts;
using Morsa.Domain.Correlation;
using Morsa.Domain.Discovery;
using Morsa.Domain.Networking;
using Morsa.Domain.Projects;
using Morsa.Domain.Recon;
using Morsa.Domain.Runs;

namespace Morsa.Application.Abstractions;

/// <summary>Unit-of-work abstraction owned by the application layer.</summary>
public interface IMorsaStore
{
    IQueryable<MorsaProject> Projects { get; }

    IQueryable<ScopeEntry> ScopeEntries { get; }

    IQueryable<Run> Runs { get; }

    IQueryable<RunTask> Tasks { get; }

    IQueryable<Artifact> Artifacts { get; }

    IQueryable<MetadataObservation> MetadataObservations { get; }

    IQueryable<Evidence> Evidence { get; }

    IQueryable<Finding> Findings { get; }

    IQueryable<EntityNode> Entities { get; }

    IQueryable<EntityRelation> Relations { get; }

    IQueryable<DiscoveredResource> DiscoveredResources { get; }

    IQueryable<ProviderRequest> ProviderRequests { get; }

    IQueryable<DnsObservation> DnsObservations { get; }

    IQueryable<ServiceObservation> ServiceObservations { get; }

    IQueryable<MalwareObservation> MalwareObservations { get; }

    IQueryable<PluginExecution> PluginExecutions { get; }

    IQueryable<ProxyPool> ProxyPools { get; }

    IQueryable<ProxyEndpoint> ProxyEndpoints { get; }

    IQueryable<ProxyHealthSample> ProxyHealthSamples { get; }

    IQueryable<ProxyLease> ProxyLeases { get; }

    IQueryable<NetworkAttempt> NetworkAttempts { get; }

    void Add<TEntity>(TEntity entity)
        where TEntity : class;

    void AddRange<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class;

    void Remove<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Initializes or upgrades the workspace database.</summary>
public interface IStoreInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Provides the effective project paths without relying on the process CWD.</summary>
public interface IWorkspaceContext
{
    string RootPath { get; }

    string DatabasePath { get; }

    string ConfigurationPath { get; }

    string ArtifactsPath { get; }

    string ReportsPath { get; }

    string LogsPath { get; }
}

/// <summary>Clock abstraction keeps retry and lease logic deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
