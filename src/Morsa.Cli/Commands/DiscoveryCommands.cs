using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Cli.Runtime;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Discovery;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class DiscoverDocumentsSettings : WorkspaceSettings
{
    [CommandArgument(0, "<TARGET>")]
    public required string Target { get; init; }

    [CommandOption("--types <TYPES>")]
    public string Types { get; init; } = "pdf,doc,docx,xls,xlsx,ppt,pptx,odt,ods,odp,svg";

    [CommandOption("--provider <PROVIDERS>")]
    public string Providers { get; init; } = "searxng,duckduckgo,commoncrawl";

    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }

    [CommandOption("--max-results <COUNT>")]
    public int MaxResults { get; init; } = 100;

    [CommandOption("--active-crawl")]
    public bool ActiveCrawl { get; init; }
}

/// <summary>Runs keyless-first provider fan-out with persistent partial coverage.</summary>
public sealed class DiscoverDocumentsCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    DiscoveryService discovery,
    CliOutput output) : AsyncCommand<DiscoverDocumentsSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        DiscoverDocumentsSettings settings,
        CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "discover documents", settings.ActiveCrawl ? ActivityMode.Active : ActivityMode.Passive, cancellationToken)
            .ConfigureAwait(false);
        var providers = settings.Providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (settings.ActiveCrawl && !providers.Contains("direct-crawler", StringComparer.OrdinalIgnoreCase))
        {
            providers.Add("direct-crawler");
        }

        var query = new SearchQuery(
            settings.Target.Trim().TrimEnd('.').ToLowerInvariant(),
            settings.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxResults: Math.Clamp(settings.MaxResults, 1, 5_000));
        var execution = new SearchExecutionContext(run.Id, null, run.Id.ToString("N"), settings.ProxyPool, query.MaxResults * query.FileTypes.Count);
        var result = await discovery.DiscoverAsync(project.Id, run.Id, query, execution, providers, cancellationToken)
            .ConfigureAwait(false);
        var status = result.FailedProviders.Count == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed;
        var coverage = result.FailedProviders.Count == 0 ? "complete" : "partial_provider_failure";
        await runs.CompleteAsync(run, status, coverage, cancellationToken).ConfigureAwait(false);
        output.Write(new { discovered = result.Added, failed_providers = result.FailedProviders }, settings.Json, run.Id.ToString(), coverage);
        return result.FailedProviders.Count == 0 ? 0 : 5;
    }
}

/// <summary>Common Crawl-only historical discovery alias.</summary>
public sealed class DiscoverHistoryCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    DiscoveryService discovery,
    CliOutput output) : AsyncCommand<DiscoverDocumentsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DiscoverDocumentsSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "discover history", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var query = new SearchQuery(settings.Target, settings.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), MaxResults: settings.MaxResults);
        var result = await discovery.DiscoverAsync(
            project.Id,
            run.Id,
            query,
            new SearchExecutionContext(run.Id, null, run.Id.ToString("N"), settings.ProxyPool, settings.MaxResults),
            ["commoncrawl"],
            cancellationToken).ConfigureAwait(false);
        var coverage = result.FailedProviders.Count == 0 ? "complete" : "partial_provider_failure";
        await runs.CompleteAsync(run, result.FailedProviders.Count == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed, coverage, cancellationToken)
            .ConfigureAwait(false);
        output.Write(new { discovered = result.Added, failed_providers = result.FailedProviders }, settings.Json, run.Id.ToString(), coverage);
        return result.FailedProviders.Count == 0 ? 0 : 5;
    }
}

public class FetchPendingSettings : WorkspaceSettings
{
    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }

    [CommandOption("--max-mb <MB>")]
    public int MaxMb { get; init; } = 100;
}

/// <summary>Downloads every pending resource and preserves failures for resume.</summary>
public sealed class FetchPendingCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    AcquisitionService acquisition,
    CliOutput output) : AsyncCommand<FetchPendingSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FetchPendingSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "fetch pending", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var result = await acquisition.FetchPendingAsync(project.Id, run.Id, settings.ProxyPool, settings.MaxMb * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        var coverage = result.Failed == 0 ? "complete" : "partial_provider_failure";
        await runs.CompleteAsync(run, result.Failed == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed, coverage, cancellationToken)
            .ConfigureAwait(false);
        output.Write(new { downloaded = result.Downloaded, failed = result.Failed }, settings.Json, run.Id.ToString(), coverage);
        return result.Failed == 0 ? 0 : 5;
    }
}

public sealed class IngestUrlSettings : FetchPendingSettings
{
    [CommandArgument(0, "<URL>")]
    public required string Url { get; init; }
}

/// <summary>Creates an explicit URL candidate and acquires it through the same secure pipeline.</summary>
public sealed class IngestUrlCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    AcquisitionService acquisition,
    CliOutput output) : AsyncCommand<IngestUrlSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, IngestUrlSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "ingest url", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var resource = new DiscoveredResource
        {
            ProjectId = project.Id,
            RunId = run.Id,
            Url = settings.Url,
            CanonicalUrl = DiscoveryUtilities.Canonicalize(settings.Url),
            ProviderId = "manual",
            Query = settings.Url,
        };
        var existing = await store.DiscoveredResources.SingleOrDefaultAsync(item => item.ProjectId == project.Id && item.CanonicalUrl == resource.CanonicalUrl, cancellationToken)
            .ConfigureAwait(false);
        resource = existing ?? resource;
        if (existing is null) store.Add(resource);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var artifact = await acquisition.FetchAsync(project.Id, run.Id, resource, settings.ProxyPool, settings.MaxMb * 1024 * 1024, 5, false, cancellationToken)
            .ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(artifact, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

/// <summary>Reports provider configuration and health without claiming untested readiness.</summary>
public sealed class ProviderListCommand(IEnumerable<ISearchProvider> providers, CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var results = new List<object>();
        foreach (var provider in providers)
        {
            var health = await provider.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new { provider = provider.Id, health.IsHealthy, health.State, health.Detail });
        }

        output.Write(results, settings.Json);
        return 0;
    }
}
