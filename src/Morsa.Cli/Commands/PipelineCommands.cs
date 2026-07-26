using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Infrastructure.Discovery;
using Morsa.Infrastructure.Pipelines;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class FullPipelineSettings : WorkspaceSettings
{
    [CommandArgument(0, "<TARGET>")]
    public required string Target { get; init; }

    [CommandOption("--types <TYPES>")]
    public string Types { get; init; } = "pdf,doc,docx,xls,xlsx,ppt,pptx,odt,ods,odp,svg";

    [CommandOption("--providers <PROVIDERS>")]
    public string Providers { get; init; } = "searxng,duckduckgo,commoncrawl";

    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }

    [CommandOption("--active-crawl")]
    public bool ActiveCrawl { get; init; }
}

/// <summary>Executes discovery, acquisition, parsing and correlation as one durable pipeline.</summary>
public sealed class FullPipelineCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    FullPipelineService pipeline,
    CliOutput output) : AsyncCommand<FullPipelineSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FullPipelineSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var result = await pipeline.RunAsync(
            project.Id,
            settings.Target.Trim().TrimEnd('.').ToLowerInvariant(),
            settings.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            settings.Providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            settings.ProxyPool,
            settings.ActiveCrawl,
            cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json, result.RunId.ToString(), result.Coverage);
        return result.Coverage == "complete" ? 0 : 5;
    }
}

public sealed class ResumePipelineSettings : WorkspaceSettings
{
    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }
}

/// <summary>Resumes pending acquisition and parser work idempotently.</summary>
public sealed class ResumePipelineCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    FullPipelineService pipeline,
    CliOutput output) : AsyncCommand<ResumePipelineSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ResumePipelineSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var result = await pipeline.ResumeAsync(project.Id, settings.ProxyPool, cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json, result.RunId.ToString(), result.Coverage);
        return result.Coverage == "complete" ? 0 : 5;
    }
}

public sealed class ProviderBootstrapSettings : WorkspaceSettings
{
    [CommandArgument(0, "<PROVIDER>")]
    public string Provider { get; init; } = "searxng";

    [CommandOption("--output <DIRECTORY>")]
    public string? Output { get; init; }
}

/// <summary>Generates a local, loopback-only provider deployment.</summary>
public sealed class ProviderBootstrapCommand(
    IWorkspaceContext workspace,
    SearXngBootstrapService bootstrap,
    CliOutput output) : AsyncCommand<ProviderBootstrapSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProviderBootstrapSettings settings, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.Provider, "searxng", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Provider '{settings.Provider}' does not provide a built-in bootstrap template.");
        }

        var root = settings.Output ?? Path.Combine(workspace.RootPath, ".morsa", "providers", "searxng");
        var files = await bootstrap.GenerateAsync(root, cancellationToken).ConfigureAwait(false);
        output.Write(new { provider = "searxng", files, url = "http://127.0.0.1:8080" }, settings.Json);
        return 0;
    }
}
