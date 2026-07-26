using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Workspace;

namespace Morsa.Infrastructure.Persistence;

/// <summary>Creates directories, applies migrations and enables SQLite WAL safely.</summary>
public sealed class StoreInitializer(
    MorsaDbContext database,
    IWorkspaceContext workspace,
    MorsaConfiguration configuration) : IStoreInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (workspace is WorkspaceContext concrete)
        {
            concrete.EnsureDirectories();
        }

        if (database.Database.GetMigrations().Any())
        {
            await database.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // EnsureCreated keeps developer builds usable before the first generated migration.
            await database.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        // WAL improves concurrent readers while a single coordinator writes journal entries.
        await database.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken)
            .ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken)
            .ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken)
            .ConfigureAwait(false);
        await ApplyProxyProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (File.Exists(workspace.ConfigurationPath))
                File.SetUnixFileMode(workspace.ConfigurationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private async Task ApplyProxyProfilesAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, profile) in configuration.ProxyProfiles)
        {
            var pool = await database.ProxyPoolSet.SingleOrDefaultAsync(item => item.Name == name, cancellationToken).ConfigureAwait(false);
            if (pool is null)
            {
                pool = new ProxyPool { Name = name };
                database.ProxyPoolSet.Add(pool);
            }

            // TOML profiles are authoritative for policy, while endpoint health remains durable in SQLite.
            pool.SelectionPolicy = Enum.Parse<ProxySelectionPolicy>(profile.Policy.Replace("-", string.Empty), true);
            pool.MaxRotations = profile.MaxRotations;
            pool.MaxAttempts = profile.MaxAttempts;
            pool.CooldownSeconds = profile.CooldownSeconds;
            pool.LeaseTtlSeconds = profile.LeaseTtlSeconds;
            pool.AllowDirectFallback = profile.AllowDirectFallback;
            pool.Enabled = true;
        }

        if (configuration.ProxyProfiles.Count > 0)
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Creates DbContext options for design-time migration commands.</summary>
public sealed class MorsaDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<MorsaDbContext>
{
    public MorsaDbContext CreateDbContext(string[] args)
    {
        var path = args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "morsa.db");
        var builder = new DbContextOptionsBuilder<MorsaDbContext>();
        builder.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());
        return new MorsaDbContext(builder.Options);
    }
}
