using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Correlation;
using Morsa.Domain.Discovery;
using Morsa.Domain.Networking;
using Morsa.Domain.Projects;
using Morsa.Domain.Recon;
using Morsa.Domain.Runs;

namespace Morsa.Infrastructure.Persistence;

/// <summary>SQLite persistence boundary for one self-contained workspace.</summary>
public sealed class MorsaDbContext(DbContextOptions<MorsaDbContext> options)
    : DbContext(options), IMorsaStore
{
    public DbSet<MorsaProject> ProjectSet => Set<MorsaProject>();

    public DbSet<ScopeEntry> ScopeEntrySet => Set<ScopeEntry>();

    public DbSet<Run> RunSet => Set<Run>();

    public DbSet<RunTask> TaskSet => Set<RunTask>();

    public DbSet<Artifact> ArtifactSet => Set<Artifact>();

    public DbSet<MetadataObservation> MetadataObservationSet => Set<MetadataObservation>();

    public DbSet<Evidence> EvidenceSet => Set<Evidence>();

    public DbSet<Finding> FindingSet => Set<Finding>();

    public DbSet<EntityNode> EntitySet => Set<EntityNode>();

    public DbSet<EntityRelation> RelationSet => Set<EntityRelation>();

    public DbSet<DiscoveredResource> DiscoveredResourceSet => Set<DiscoveredResource>();
    public DbSet<ProviderRequest> ProviderRequestSet => Set<ProviderRequest>();
    public DbSet<DnsObservation> DnsObservationSet => Set<DnsObservation>();
    public DbSet<ServiceObservation> ServiceObservationSet => Set<ServiceObservation>();
    public DbSet<MalwareObservation> MalwareObservationSet => Set<MalwareObservation>();
    public DbSet<PluginExecution> PluginExecutionSet => Set<PluginExecution>();

    public DbSet<ProxyPool> ProxyPoolSet => Set<ProxyPool>();

    public DbSet<ProxyEndpoint> ProxyEndpointSet => Set<ProxyEndpoint>();

    public DbSet<ProxyHealthSample> ProxyHealthSampleSet => Set<ProxyHealthSample>();

    public DbSet<ProxyLease> ProxyLeaseSet => Set<ProxyLease>();

    public DbSet<NetworkAttempt> NetworkAttemptSet => Set<NetworkAttempt>();

    IQueryable<MorsaProject> IMorsaStore.Projects => ProjectSet;
    IQueryable<ScopeEntry> IMorsaStore.ScopeEntries => ScopeEntrySet;
    IQueryable<Run> IMorsaStore.Runs => RunSet;
    IQueryable<RunTask> IMorsaStore.Tasks => TaskSet;
    IQueryable<Artifact> IMorsaStore.Artifacts => ArtifactSet;
    IQueryable<MetadataObservation> IMorsaStore.MetadataObservations => MetadataObservationSet;
    IQueryable<Evidence> IMorsaStore.Evidence => EvidenceSet;
    IQueryable<Finding> IMorsaStore.Findings => FindingSet;
    IQueryable<EntityNode> IMorsaStore.Entities => EntitySet;
    IQueryable<EntityRelation> IMorsaStore.Relations => RelationSet;
    IQueryable<DiscoveredResource> IMorsaStore.DiscoveredResources => DiscoveredResourceSet;
    IQueryable<ProviderRequest> IMorsaStore.ProviderRequests => ProviderRequestSet;
    IQueryable<DnsObservation> IMorsaStore.DnsObservations => DnsObservationSet;
    IQueryable<ServiceObservation> IMorsaStore.ServiceObservations => ServiceObservationSet;
    IQueryable<MalwareObservation> IMorsaStore.MalwareObservations => MalwareObservationSet;
    IQueryable<PluginExecution> IMorsaStore.PluginExecutions => PluginExecutionSet;
    IQueryable<ProxyPool> IMorsaStore.ProxyPools => ProxyPoolSet;
    IQueryable<ProxyEndpoint> IMorsaStore.ProxyEndpoints => ProxyEndpointSet;
    IQueryable<ProxyHealthSample> IMorsaStore.ProxyHealthSamples => ProxyHealthSampleSet;
    IQueryable<ProxyLease> IMorsaStore.ProxyLeases => ProxyLeaseSet;
    IQueryable<NetworkAttempt> IMorsaStore.NetworkAttempts => NetworkAttemptSet;

    void IMorsaStore.Add<TEntity>(TEntity entity) => Add(entity);

    void IMorsaStore.AddRange<TEntity>(IEnumerable<TEntity> entities) => AddRange(entities);

    void IMorsaStore.Remove<TEntity>(TEntity entity) => Remove(entity);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MorsaProject>().HasIndex(project => project.RootPath).IsUnique();
        modelBuilder.Entity<ScopeEntry>().HasIndex(entry => new { entry.ProjectId, entry.Kind, entry.Value }).IsUnique();
        modelBuilder.Entity<RunTask>().HasIndex(task => new { task.RunId, task.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<Artifact>().HasIndex(artifact => new { artifact.RunId, artifact.Sha256 });
        modelBuilder.Entity<MetadataObservation>().HasIndex(observation => observation.ArtifactId);
        modelBuilder.Entity<EntityNode>().HasIndex(entity => new { entity.ProjectId, entity.Type, entity.NormalizedValue }).IsUnique();
        modelBuilder.Entity<DiscoveredResource>().HasIndex(resource => new { resource.ProjectId, resource.CanonicalUrl }).IsUnique();
        modelBuilder.Entity<ProviderRequest>().HasIndex(request => new { request.RunId, request.ProviderId, request.Query });
        modelBuilder.Entity<DnsObservation>().HasIndex(item => new { item.RunId, item.Name, item.RecordType, item.Value });
        modelBuilder.Entity<ServiceObservation>().HasIndex(item => new { item.RunId, item.Host, item.Port, item.Protocol });
        modelBuilder.Entity<MalwareObservation>().HasIndex(item => new { item.ArtifactId, item.Kind, item.Value });
        modelBuilder.Entity<ProxyPool>().HasIndex(pool => pool.Name).IsUnique();
        modelBuilder.Entity<ProxyEndpoint>().HasIndex(endpoint => new { endpoint.PoolId, endpoint.Uri }).IsUnique();
        modelBuilder.Entity<ProxyLease>().HasIndex(lease => new { lease.SessionKey, lease.ReleasedAt });
        modelBuilder.Entity<NetworkAttempt>().HasIndex(attempt => attempt.AttemptedAt);
    }
}
