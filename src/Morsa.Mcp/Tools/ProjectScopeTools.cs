using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;

namespace Morsa.Mcp.Tools;

/// <summary>Workspace and authorization-scope tools exposed through MCP stdio.</summary>
[McpServerToolType]
public static class ProjectScopeTools
{
    [McpServerTool(Name = "morsa_project_init")]
    [Description("Creates or opens a Morsa workspace and returns its stable project identity.")]
    public static async Task<object> ProjectInit(
        [Description("Workspace directory to create or open.")] string path,
        [Description("Optional project display name.")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.CreateAsync(path, name, cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            project = new
            {
                id = context.Project.Id,
                context.Project.Name,
                root_path = context.Project.RootPath,
                default_mode = context.Project.DefaultMode.ToString().ToLowerInvariant(),
                context.Project.CreatedAt,
            },
        };
    }

    [McpServerTool(Name = "morsa_project_status")]
    [Description("Returns durable workspace counters and recent run states.")]
    public static async Task<object> ProjectStatus(
        [Description("Initialized Morsa workspace directory.")] string path,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var store = context.Store;
        var runIds = store.Runs.Where(run => run.ProjectId == context.Project.Id).Select(run => run.Id);
        var artifactIds = store.Artifacts.Where(artifact => runIds.Contains(artifact.RunId)).Select(artifact => artifact.Id);
        // SQLite cannot translate DateTimeOffset ORDER BY; order the project-sized run set in memory.
        var recentRuns = (await store.Runs.Where(run => run.ProjectId == context.Project.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(run => run.CreatedAt)
            .Take(20)
            .ToArray();
        return new
        {
            schema_version = McpContract.SchemaVersion,
            project = new { id = context.Project.Id, context.Project.Name, root_path = context.Project.RootPath },
            counts = new
            {
                scopes = await store.ScopeEntries.CountAsync(item => item.ProjectId == context.Project.Id, cancellationToken).ConfigureAwait(false),
                runs = await runIds.CountAsync(cancellationToken).ConfigureAwait(false),
                artifacts = await artifactIds.CountAsync(cancellationToken).ConfigureAwait(false),
                observations = await store.MetadataObservations.CountAsync(item => artifactIds.Contains(item.ArtifactId), cancellationToken).ConfigureAwait(false),
                entities = await store.Entities.CountAsync(item => item.ProjectId == context.Project.Id, cancellationToken).ConfigureAwait(false),
                findings = await store.Findings.CountAsync(item => runIds.Contains(item.RunId), cancellationToken).ConfigureAwait(false),
            },
            recent_runs = recentRuns,
        };
    }

    [McpServerTool(Name = "morsa_scope_add")]
    [Description("Adds or updates an authorized scope entry and its maximum activity mode.")]
    public static async Task<object> ScopeAdd(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Domain, host, URL or IP address to authorize.")] string value,
        [Description("Optional kind: domain, host, url, ip or cidr.")] string? kind = null,
        [Description("Maximum mode: passive, active or aggressive.")] string maximum_mode = "active",
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var normalizedKind = (kind ?? WorkspacePathPolicy.InferScopeKind(value)).Trim().ToLowerInvariant();
        var normalizedValue = WorkspacePathPolicy.NormalizeScopeValue(value, normalizedKind);
        if (!Enum.TryParse<ActivityMode>(maximum_mode, true, out var mode))
        {
            throw new ArgumentException("Maximum mode must be passive, active or aggressive.", nameof(maximum_mode));
        }

        var entry = await context.Store.ScopeEntries.SingleOrDefaultAsync(
            item => item.ProjectId == context.Project.Id && item.Kind == normalizedKind && item.Value == normalizedValue,
            cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            entry = new ScopeEntry
            {
                ProjectId = context.Project.Id,
                Kind = normalizedKind,
                Value = normalizedValue,
                MaximumMode = mode,
            };
            context.Store.Add(entry);
        }
        else
        {
            entry.MaximumMode = mode;
        }

        await context.Store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new { schema_version = McpContract.SchemaVersion, scope = entry };
    }

    [McpServerTool(Name = "morsa_scope_list")]
    [Description("Lists authorized scope entries for a Morsa project.")]
    public static async Task<object> ScopeList(
        [Description("Initialized Morsa workspace directory.")] string path,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var entries = await context.Store.ScopeEntries
            .Where(item => item.ProjectId == context.Project.Id)
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Value)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new { schema_version = McpContract.SchemaVersion, scopes = entries };
    }
}
