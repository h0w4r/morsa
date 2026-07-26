using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Pipelines;
using Morsa.Infrastructure.Reporting;

namespace Morsa.Mcp.Tools;

/// <summary>Read models, safe report exports and durable end-to-end pipelines.</summary>
[McpServerToolType]
public static class ReportingPipelineTools
{
    [McpServerTool(Name = "morsa_get_entities")]
    [Description("Returns a bounded page of normalized project entities.")]
    public static async Task<object> GetEntities(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Optional exact entity type filter.")] string? type = null,
        [Description("Number of records to skip.")] int offset = 0,
        [Description("Maximum records to return, up to 10000.")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var query = context.Store.Entities.Where(item => item.ProjectId == context.Project.Id);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(item => item.Type == type.Trim().ToLower());
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var entities = await query.OrderBy(item => item.Type).ThenBy(item => item.NormalizedValue)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 10_000))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            total,
            offset = Math.Max(0, offset),
            count = entities.Length,
            entities,
        };
    }

    [McpServerTool(Name = "morsa_get_findings")]
    [Description("Returns a bounded page of findings belonging to the selected project.")]
    public static async Task<object> GetFindings(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Optional minimum severity: informational, low, medium, high or critical.")] string? minimum_severity = null,
        [Description("Number of records to skip.")] int offset = 0,
        [Description("Maximum records to return, up to 10000.")] int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var runIds = context.Store.Runs.Where(run => run.ProjectId == context.Project.Id).Select(run => run.Id);
        var query = context.Store.Findings.Where(item => runIds.Contains(item.RunId));
        if (!string.IsNullOrWhiteSpace(minimum_severity))
        {
            if (!Enum.TryParse<FindingSeverity>(minimum_severity, true, out var severity))
            {
                throw new ArgumentException("Minimum severity must be informational, low, medium, high or critical.", nameof(minimum_severity));
            }

            query = query.Where(item => item.Severity >= severity);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var findings = await query.OrderByDescending(item => item.Severity).ThenBy(item => item.RuleId)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 10_000))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            total,
            offset = Math.Max(0, offset),
            count = findings.Length,
            findings,
        };
    }

    [McpServerTool(Name = "morsa_export_graph")]
    [Description("Exports the evidence graph as DOT, GraphML, GEXF or CSV below workspace reports/.")]
    public static async Task<object> ExportGraph(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Output format: dot, graphml, gexf or csv.")] string format = "graphml",
        [Description("Optional output path confined to workspace reports/.")] string? output = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("dot" or "graphml" or "gexf" or "csv"))
        {
            throw new ArgumentException("Format must be dot, graphml, gexf or csv.", nameof(format));
        }

        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var outputPath = WorkspacePathPolicy.ResolveReportOutput(context.Workspace, output, $"morsa-graph.{normalizedFormat}");
        await context.GetRequiredService<GraphExporter>()
            .ExportAsync(context.Project.Id, normalizedFormat, outputPath, cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            format = normalizedFormat,
            output = outputPath,
            bytes = new FileInfo(outputPath).Length,
        };
    }

    [McpServerTool(Name = "morsa_export_report")]
    [Description("Exports a redacted project report as JSON or script-free HTML below workspace reports/.")]
    public static async Task<object> ExportReport(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Output format: json or html.")] string format = "json",
        [Description("Optional output path confined to workspace reports/.")] string? output = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("json" or "html"))
        {
            throw new ArgumentException("Format must be json or html.", nameof(format));
        }

        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var report = await BuildReportAsync(context, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(report, ReportJsonContext.Default);
        var content = normalizedFormat == "json" ? json : BuildHtml(json);
        var outputPath = WorkspacePathPolicy.ResolveReportOutput(context.Workspace, output, $"morsa-report.{normalizedFormat}");
        await WriteAtomicallyAsync(outputPath, content, cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            format = normalizedFormat,
            output = outputPath,
            bytes = new FileInfo(outputPath).Length,
        };
    }

    [McpServerTool(Name = "morsa_run_full")]
    [Description("Runs discovery, acquisition, metadata analysis and correlation as one durable pipeline.")]
    public static async Task<object> RunFull(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Authorized target domain.")] string target,
        [Description("Document extensions to discover.")] string[]? types = null,
        [Description("Discovery provider IDs.")] string[]? providers = null,
        [Description("Optional configured proxy pool name.")] string? proxy_pool = null,
        [Description("Enables the active direct crawler provider.")] bool active_crawl = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        await NetworkToolPolicy.RequireScopeAsync(
            context,
            target.Trim().TrimEnd('.'),
            443,
            active_crawl ? ActivityMode.Active : ActivityMode.Passive,
            cancellationToken).ConfigureAwait(false);
        var selectedTypes = types is { Length: > 0 }
            ? types.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToArray()
            : new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "svg" };
        var selectedProviders = providers is { Length: > 0 }
            ? providers.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : new[] { "searxng", "duckduckgo", "commoncrawl" };
        var result = await context.GetRequiredService<FullPipelineService>().RunAsync(
            context.Project.Id,
            target.Trim().TrimEnd('.').ToLowerInvariant(),
            selectedTypes,
            selectedProviders,
            proxy_pool,
            active_crawl,
            cancellationToken).ConfigureAwait(false);
        return new { schema_version = McpContract.SchemaVersion, result };
    }

    [McpServerTool(Name = "morsa_run_resume")]
    [Description("Resumes pending acquisition, parsing and correlation work idempotently.")]
    public static async Task<object> RunResume(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Optional configured proxy pool name.")] string? proxy_pool = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var result = await context.GetRequiredService<FullPipelineService>()
            .ResumeAsync(context.Project.Id, proxy_pool, cancellationToken).ConfigureAwait(false);
        return new { schema_version = McpContract.SchemaVersion, result };
    }

    private static async Task<object> BuildReportAsync(WorkspaceToolContext context, CancellationToken cancellationToken)
    {
        var runs = await context.Store.Runs.Where(run => run.ProjectId == context.Project.Id)
            .OrderBy(run => run.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var runIds = runs.Select(run => run.Id).ToArray();
        var artifacts = await context.Store.Artifacts.Where(artifact => runIds.Contains(artifact.RunId))
            .OrderBy(artifact => artifact.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = artifacts.Select(artifact => artifact.Id).ToArray();
        var observations = await context.Store.MetadataObservations.Where(item => artifactIds.Contains(item.ArtifactId))
            .OrderBy(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var entities = await context.Store.Entities.Where(item => item.ProjectId == context.Project.Id)
            .OrderBy(item => item.Type).ThenBy(item => item.NormalizedValue).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var findings = await context.Store.Findings.Where(item => runIds.Contains(item.RunId))
            .OrderByDescending(item => item.Severity).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var services = await context.Store.ServiceObservations.Where(item => runIds.Contains(item.RunId))
            .OrderBy(item => item.Host).ThenBy(item => item.Port).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            redacted = true,
            project = new
            {
                id = context.Project.Id,
                context.Project.Name,
                default_mode = context.Project.DefaultMode.ToString().ToLowerInvariant(),
                context.Project.CreatedAt,
            },
            runs,
            artifacts = artifacts.Select(artifact => new
            {
                artifact.Id,
                artifact.RunId,
                SourceUri = RedactUri(artifact.SourceUri),
                artifact.Sha256,
                artifact.Size,
                artifact.MimeType,
                artifact.Kind,
                artifact.AcquiredAt,
            }).ToArray(),
            observations = observations.Select(item => SensitiveTypes.Contains(item.Category)
                ? (object)new
                {
                    item.Id,
                    item.ArtifactId,
                    item.Category,
                    OriginalValue = Redact(item.OriginalValue),
                    NormalizedValue = Redact(item.NormalizedValue),
                    item.Extractor,
                    item.ExtractorVersion,
                    Location = item.Location is null ? null : Redact(item.Location),
                    item.Confidence,
                    item.ObservedAt,
                }
                : item).ToArray(),
            evidence = (await context.Store.Evidence.Where(item => artifactIds.Contains(item.ArtifactId)).OrderBy(item => item.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false)).Select(item => new
                {
                    item.Id,
                    item.ArtifactId,
                    item.ObservationId,
                    item.Source,
                    Value = Redact(item.Value),
                    Location = item.Location is null ? null : Redact(item.Location),
                    item.ArtifactSha256,
                    item.CollectedAt,
                }).ToArray(),
            entities = entities.Select(item => SensitiveTypes.Contains(item.Type)
                ? (object)new { item.Id, item.ProjectId, item.Type, Value = Redact(item.Value), NormalizedValue = Redact(item.NormalizedValue), item.Confidence }
                : item).ToArray(),
            relations = await context.Store.Relations.Where(item => item.ProjectId == context.Project.Id)
                .OrderBy(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false),
            findings = findings.Select(item => item.Sensitive
                ? (object)new { item.Id, item.RunId, item.ArtifactId, item.RuleId, item.Title, Description = Redact(item.Description), item.Severity, item.Confidence, item.Sensitive }
                : item).ToArray(),
            discovered_resources = (await context.Store.DiscoveredResources.Where(item => item.ProjectId == context.Project.Id)
                .OrderBy(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false)).Select(item => new
                {
                    item.Id,
                    item.ProjectId,
                    item.RunId,
                    Url = RedactUri(item.Url),
                    CanonicalUrl = RedactUri(item.CanonicalUrl),
                    item.ProviderId,
                    Query = Redact(item.Query),
                    item.Title,
                    item.Snippet,
                    item.Status,
                    item.LastError,
                    item.DiscoveredAt,
                }).ToArray(),
            dns = await context.Store.DnsObservations.Where(item => runIds.Contains(item.RunId))
                .OrderBy(item => item.Name).ThenBy(item => item.RecordType).ToArrayAsync(cancellationToken).ConfigureAwait(false),
            services = services.Select(item => new
            {
                item.Id,
                item.RunId,
                Host = Redact(item.Host),
                item.Port,
                item.Protocol,
                Banner = item.Banner is null ? null : Redact(item.Banner),
                item.TlsSubject,
                item.TlsIssuer,
                item.Technology,
                item.ObservedAt,
            }).ToArray(),
            malware = await context.Store.MalwareObservations.Where(item => artifactIds.Contains(item.ArtifactId))
                .OrderBy(item => item.ArtifactId).ThenBy(item => item.Kind).ToArrayAsync(cancellationToken).ConfigureAwait(false),
            provider_requests = (await context.Store.ProviderRequests.Where(item => runIds.Contains(item.RunId)).OrderBy(item => item.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false)).Select(item => new
                {
                    item.Id,
                    item.RunId,
                    item.ProviderId,
                    Query = Redact(item.Query),
                    item.Status,
                    item.AttemptCount,
                    item.LastCursor,
                    item.NextRetryAt,
                    item.CoverageTagsJson,
                    item.LastError,
                }).ToArray(),
            network_attempts = await context.Store.NetworkAttempts.Where(item => item.RunId.HasValue && runIds.Contains(item.RunId.Value))
                .OrderBy(item => item.AttemptedAt).ToArrayAsync(cancellationToken).ConfigureAwait(false),
            proxy_summary = await context.Store.ProxyEndpoints
                .OrderBy(item => item.Protocol)
                .Select(item => new { item.Protocol, item.DnsMode, item.Status, item.SuccessCount, item.FailureCount, item.EwmaLatencyMs })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            timeline = BuildTimeline(runs, artifacts, observations),
        };
    }

    private static readonly HashSet<string> SensitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "last_saved_by", "email", "username", "hostname", "server", "domain", "url", "path",
        "unc_path", "gps", "password", "credential", "credential_indicator", "manager",
    };

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
        return $"[redacted:{digest}]";
    }

    private static string? RedactUri(string? value)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value is null ? null : Redact(value);
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private static object[] BuildTimeline(
        IEnumerable<Morsa.Domain.Runs.Run> runs,
        IEnumerable<Morsa.Domain.Artifacts.Artifact> artifacts,
        IEnumerable<Morsa.Domain.Artifacts.MetadataObservation> observations)
    {
        var timeline = new List<(DateTimeOffset Timestamp, object Entry)>();
        foreach (var run in runs)
        {
            timeline.Add((run.CreatedAt, new { timestamp = run.CreatedAt, kind = "run_created", source_id = run.Id }));
            if (run.StartedAt is { } started) timeline.Add((started, new { timestamp = started, kind = "run_started", source_id = run.Id }));
            if (run.FinishedAt is { } finished) timeline.Add((finished, new { timestamp = finished, kind = "run_finished", source_id = run.Id }));
        }
        foreach (var artifact in artifacts)
            timeline.Add((artifact.AcquiredAt, new { timestamp = artifact.AcquiredAt, kind = "artifact_acquired", source_id = artifact.Id }));
        foreach (var observation in observations.Where(item => item.Category.Contains("date", StringComparison.OrdinalIgnoreCase)))
        {
            if (!DateTimeOffset.TryParse(observation.NormalizedValue, out var timestamp) &&
                !DateTimeOffset.TryParse(observation.OriginalValue, out timestamp)) continue;
            timeline.Add((timestamp, new { timestamp, kind = "metadata_date", source_id = observation.Id }));
        }
        return timeline.OrderBy(item => item.Timestamp).Select(item => item.Entry).ToArray();
    }

    private static string BuildHtml(string json)
    {
        var encoded = WebUtility.HtmlEncode(json);
        return "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width\">" +
               "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">" +
               "<title>Morsa report</title><style>body{font:15px system-ui;margin:2rem;max-width:1100px}" +
               "pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#111;color:#ddd;padding:1rem}</style>" +
               $"</head><body><h1>Morsa report</h1><p>Schema {McpContract.SchemaVersion}</p><pre>{encoded}</pre></body></html>";
    }

    private static async Task WriteAtomicallyAsync(string outputPath, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

/// <summary>Central JSON settings for deterministic, versioned report contracts.</summary>
internal static class ReportJsonContext
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
