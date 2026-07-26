using System.Net;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class ReportSettings : WorkspaceSettings
{
    [CommandOption("--output <FILE>")]
    public string? Output { get; init; }
}

internal sealed record ProjectReport(
    string SchemaVersion,
    object Project,
    object[] Runs,
    object[] Artifacts,
    object[] Observations,
    object[] Entities,
    object[] Findings,
    object[] ProxySummary);

/// <summary>Exports a complete versioned JSON report.</summary>
public sealed class ReportJsonCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ReportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var report = await BuildReportAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var path = settings.Output ?? Path.Combine(workspace.ReportsPath, "morsa-report.json");
        output.WriteJsonFile(path, report);
        output.Write(new { output = Path.GetFullPath(path) }, settings.Json);
        return 0;
    }

    internal static async Task<ProjectReport> BuildReportAsync(
        IStoreInitializer initializer,
        IMorsaStore store,
        IWorkspaceContext workspace,
        CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        return new ProjectReport(
            BuildInfo.SchemaVersion,
            new { project.Id, project.Name, project.RootPath, project.DefaultMode, project.CreatedAt },
            await store.Runs.Where(item => item.ProjectId == project.Id).Cast<object>().ToArrayAsync(cancellationToken),
            await store.Artifacts.Cast<object>().ToArrayAsync(cancellationToken),
            await store.MetadataObservations.Cast<object>().ToArrayAsync(cancellationToken),
            await store.Entities.Where(item => item.ProjectId == project.Id).Cast<object>().ToArrayAsync(cancellationToken),
            await store.Findings.Cast<object>().ToArrayAsync(cancellationToken),
            await store.ProxyEndpoints.Select(item => new { item.Protocol, item.Status, item.SuccessCount, item.FailureCount }).Cast<object>()
                .ToArrayAsync(cancellationToken));
    }
}

/// <summary>Produces a standalone encoded HTML summary with no active scripts.</summary>
public sealed class ReportHtmlCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ReportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var report = await ReportJsonCommand.BuildReportAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var json = WebUtility.HtmlEncode(CliOutput.ToJson(report));
        var html = "<!doctype html>\n" +
                   "<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">\n" +
                   "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">\n" +
                   "<title>Morsa report</title><style>body{font:15px system-ui;margin:2rem;max-width:1100px}" +
                   "pre{white-space:pre-wrap;background:#111;color:#ddd;padding:1rem}</style>\n" +
                   $"</head><body><h1>Morsa report</h1><p>Schema {BuildInfo.SchemaVersion}</p><pre>{json}</pre></body></html>";
        var path = settings.Output ?? Path.Combine(workspace.ReportsPath, "morsa-report.html");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, html, cancellationToken).ConfigureAwait(false);
        output.Write(new { output = Path.GetFullPath(path) }, settings.Json);
        return 0;
    }
}

