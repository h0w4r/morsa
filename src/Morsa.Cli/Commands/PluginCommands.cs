using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Infrastructure.Plugins;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class PluginInstallSettings : WorkspaceSettings
{
    [CommandArgument(0, "<SOURCE>")]
    public required string Source { get; init; }

    [CommandOption("--no-activate")]
    public bool NoActivate { get; init; }
}

public sealed class PluginInstallCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<PluginInstallSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginInstallSettings settings, CancellationToken cancellationToken)
    {
        var installed = await catalog.InstallAsync(settings.Source, !settings.NoActivate, cancellationToken).ConfigureAwait(false);
        output.Write(installed, settings.Json);
        return 0;
    }
}

public sealed class PluginListCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        output.Write(await catalog.ListAsync(cancellationToken).ConfigureAwait(false), settings.Json);
        return 0;
    }
}

public sealed class PluginInspectSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    public required string Id { get; init; }
}

public sealed class PluginInspectCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<PluginInspectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginInspectSettings settings, CancellationToken cancellationToken)
    {
        var versions = (await catalog.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(plugin => plugin.Manifest.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length == 0) throw new KeyNotFoundException($"Plugin '{settings.Id}' is not installed.");
        output.Write(versions, settings.Json);
        return versions.All(plugin => plugin.IsValid) ? 0 : 8;
    }
}

public sealed class PluginActivateSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    public required string Id { get; init; }

    [CommandArgument(1, "<VERSION>")]
    public required string Version { get; init; }
}

public sealed class PluginActivateCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<PluginActivateSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginActivateSettings settings, CancellationToken cancellationToken)
    {
        await catalog.ActivateAsync(settings.Id, settings.Version, cancellationToken).ConfigureAwait(false);
        output.Write(new { plugin = settings.Id, version = settings.Version, active = true }, settings.Json);
        return 0;
    }
}

public class PluginRollbackSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    public required string Id { get; init; }
}

public sealed class PluginRollbackCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<PluginRollbackSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginRollbackSettings settings, CancellationToken cancellationToken)
    {
        var version = await catalog.RollbackAsync(settings.Id, cancellationToken).ConfigureAwait(false);
        output.Write(new { plugin = settings.Id, version, rolled_back = true }, settings.Json);
        return 0;
    }
}

public sealed class PluginRemoveSettings : PluginRollbackSettings
{
    [CommandOption("--version <VERSION>")]
    public string? Version { get; init; }
}

public sealed class PluginRemoveCommand(PluginCatalogService catalog, CliOutput output) : AsyncCommand<PluginRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginRemoveSettings settings, CancellationToken cancellationToken)
    {
        await catalog.RemoveAsync(settings.Id, settings.Version, cancellationToken).ConfigureAwait(false);
        output.Write(new { plugin = settings.Id, version = settings.Version, removed = true }, settings.Json);
        return 0;
    }
}

public sealed class PluginRunSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ID>")]
    public required string Id { get; init; }

    [CommandArgument(1, "<OPERATION>")]
    public required string Operation { get; init; }

    [CommandOption("--input <JSON>")]
    public string Input { get; init; } = "{}";

    [CommandOption("--timeout <SECONDS>")]
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class PluginRunCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    PluginProcessRunner runner,
    CliOutput output) : AsyncCommand<PluginRunSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, PluginRunSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(settings.Input);
        var result = await runner.RunAsync(
            settings.Id,
            settings.Operation,
            document.RootElement.Clone(),
            null,
            TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 3600)),
            cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json);
        return result.ExitCode == 0 ? 0 : 9;
    }
}
