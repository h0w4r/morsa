using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class ScopeAddSettings : WorkspaceSettings
{
    [CommandArgument(0, "<VALUE>")]
    public required string Value { get; init; }

    [CommandOption("--kind <KIND>")]
    public string? Kind { get; init; }

    [CommandOption("--max-mode <MODE>")]
    public string MaximumMode { get; init; } = "active";
}

/// <summary>Adds one normalized authorized scope entry.</summary>
public sealed class ScopeAddCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ScopeAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ScopeAddSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var kind = settings.Kind?.ToLowerInvariant() ?? CommandHelpers.InferScopeKind(settings.Value);
        if (!Enum.TryParse<ActivityMode>(settings.MaximumMode, true, out var mode))
        {
            throw new InvalidOperationException("Mode must be passive, active or aggressive.");
        }

        var value = settings.Value.Trim().TrimEnd('.').ToLowerInvariant();
        var entry = await store.ScopeEntries.SingleOrDefaultAsync(
            item => item.ProjectId == project.Id && item.Kind == kind && item.Value == value,
            cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            entry = new ScopeEntry { ProjectId = project.Id, Kind = kind, Value = value, MaximumMode = mode };
            store.Add(entry);
        }
        else
        {
            entry.MaximumMode = mode;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        output.Write(entry, settings.Json);
        return 0;
    }
}

/// <summary>Lists scope entries with their maximum activity mode.</summary>
public sealed class ScopeListCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var entries = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        output.Write(entries, settings.Json);
        return 0;
    }
}


