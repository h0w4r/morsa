using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Cli.Runtime;
using Morsa.Domain.Common;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Malware;
using Morsa.Infrastructure.Recon;
using Morsa.Infrastructure.Reporting;
using Morsa.Infrastructure.Web;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class ReconDnsSettings : WorkspaceSettings
{
    [CommandArgument(0, "<NAME>")]
    public required string Name { get; init; }

    [CommandOption("--types <TYPES>")]
    public string Types { get; init; } = "A,AAAA,MX,NS,SOA,TXT,CNAME,SRV,CAA";
}

/// <summary>Queries the requested DNS record types and journals the run.</summary>
public sealed class ReconDnsCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    DnsReconService dns,
    CliOutput output) : AsyncCommand<ReconDnsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconDnsSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "recon dns", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var types = settings.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.Parse<QueryType>(value, true)).ToArray();
        var observations = await dns.QueryAsync(run.Id, settings.Name, types, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(observations, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class ReconReverseSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ADDRESSES>")]
    public required string Addresses { get; init; }
}

/// <summary>Resolves comma-separated IP addresses to PTR records.</summary>
public sealed class ReconReverseCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    DnsReconService dns,
    CliOutput output) : AsyncCommand<ReconReverseSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconReverseSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "recon reverse", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var addresses = settings.Addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPAddress.Parse);
        var results = await dns.ReverseAsync(run.Id, addresses, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(results, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public class FingerprintHttpSettings : WorkspaceSettings
{
    [CommandArgument(0, "<URL>")]
    public required string Url { get; init; }

    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }
}

/// <summary>Fingerprints an HTTP endpoint after explicit scope validation.</summary>
public sealed class FingerprintHttpCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    ScopePolicy scopePolicy,
    FingerprintService fingerprint,
    CliOutput output) : AsyncCommand<FingerprintHttpSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FingerprintHttpSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var uri = new Uri(settings.Url);
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(uri, ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint http", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var result = await fingerprint.FingerprintHttpAsync(run.Id, uri, settings.ProxyPool, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public class HostPortSettings : WorkspaceSettings
{
    [CommandArgument(0, "<HOST>")]
    public required string Host { get; init; }

    [CommandOption("--port <PORT>")]
    public int Port { get; init; }
}

/// <summary>Collects TLS certificate evidence.</summary>
public sealed class FingerprintTlsCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, FingerprintService fingerprint, CliOutput output) : AsyncCommand<HostPortSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, HostPortSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var port = settings.Port == 0 ? 443 : settings.Port;
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{settings.Host}:{port}/"), ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint tls", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var result = await fingerprint.InspectTlsAsync(run.Id, settings.Host, port, null, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class FingerprintBannerSettings : HostPortSettings
{
    [CommandOption("--protocol <PROTOCOL>")]
    public string Protocol { get; init; } = "tcp";
}

/// <summary>Collects one bounded raw service banner.</summary>
public sealed class FingerprintBannerCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, FingerprintService fingerprint, CliOutput output) : AsyncCommand<FingerprintBannerSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FingerprintBannerSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        if (settings.Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{settings.Host}:{settings.Port}/"), ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint banner", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var result = await fingerprint.GrabBannerAsync(run.Id, settings.Host, settings.Port, settings.Protocol, null, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(result, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class WebCrawlSettings : FingerprintHttpSettings
{
    [CommandOption("--depth <DEPTH>")]
    public int Depth { get; init; } = 3;

    [CommandOption("--max-pages <COUNT>")]
    public int MaxPages { get; init; } = 500;
}

public sealed class WebCrawlCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    WebMappingService web, CliOutput output) : AsyncCommand<WebCrawlSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WebCrawlSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "web crawl", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var results = await web.CrawlAsync(project.Id, run.Id, new Uri(settings.Url), Math.Clamp(settings.Depth, 0, 10), Math.Clamp(settings.MaxPages, 1, 100_000), settings.ProxyPool, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(new { discovered = results.Count }, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class WebBackupSettings : FingerprintHttpSettings
{
    [CommandOption("--budget <COUNT>")]
    public int Budget { get; init; } = 100;
}

public sealed class WebBackupCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    WebMappingService web, CliOutput output) : AsyncCommand<WebBackupSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WebBackupSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "web backups", ActivityMode.Aggressive, cancellationToken).ConfigureAwait(false);
        var findings = await web.ValidateBackupCandidatesAsync(project.Id, run.Id, new Uri(settings.Url), Math.Clamp(settings.Budget, 1, 10_000), settings.ProxyPool, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(findings, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public class MalwareScanSettings : WorkspaceSettings
{
    [CommandOption("--artifact <ID>")]
    public Guid? ArtifactId { get; init; }
}

public sealed class MalwareScanCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    MalwareAnalysisService malware, CliOutput output) : AsyncCommand<MalwareScanSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MalwareScanSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var artifacts = await store.Artifacts.Where(item => settings.ArtifactId == null || item.Id == settings.ArtifactId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var artifact in artifacts) count += (await malware.AnalyzeAsync(artifact, cancellationToken).ConfigureAwait(false)).Count;
        output.Write(new { artifacts = artifacts.Count, observations = count }, settings.Json);
        return 0;
    }
}

public sealed class YaraScanSettings : MalwareScanSettings
{
    [CommandArgument(0, "<RULES>")]
    public required string Rules { get; init; }
}

public sealed class YaraScanCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    MalwareAnalysisService malware, CliOutput output) : AsyncCommand<YaraScanSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, YaraScanSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var artifacts = await store.Artifacts.Where(item => settings.ArtifactId == null || item.Id == settings.ArtifactId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<object>();
        var failed = false;
        foreach (var artifact in artifacts)
        {
            var result = await malware.RunYaraAsync(artifact, settings.Rules, cancellationToken).ConfigureAwait(false);
            results.Add(new { artifact_id = artifact.Id, result.ExitCode, result.Output });
            failed |= result.ExitCode > 1;
        }
        output.Write(results, settings.Json);
        return failed ? 5 : 0;
    }
}

public sealed class GraphExportSettings : WorkspaceSettings
{
    [CommandOption("--format <FORMAT>")]
    public string Format { get; init; } = "graphml";

    [CommandOption("--output <FILE>")]
    public string? Output { get; init; }
}

public sealed class GraphExportCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    GraphExporter graph, CliOutput output) : AsyncCommand<GraphExportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GraphExportSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var path = settings.Output ?? Path.Combine(workspace.ReportsPath, $"morsa-graph.{settings.Format}");
        await graph.ExportAsync(project.Id, settings.Format, path, cancellationToken).ConfigureAwait(false);
        output.Write(new { output = Path.GetFullPath(path), format = settings.Format }, settings.Json);
        return 0;
    }
}
