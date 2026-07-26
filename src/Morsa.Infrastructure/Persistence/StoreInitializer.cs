using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Workspace;

namespace Morsa.Infrastructure.Persistence;

/// <summary>Creates directories, applies migrations and enables SQLite WAL safely.</summary>
public sealed class StoreInitializer(
    MorsaDbContext database,
    IWorkspaceContext workspace) : IStoreInitializer
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
