using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Correlation;
using Morsa.Infrastructure.Configuration;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public class ReportSettings : WorkspaceSettings
{
    [CommandOption("--output <FILE>")]
    public string? Output { get; init; }

    [CommandOption("--include-sensitive")]
    public bool IncludeSensitive { get; init; }
}

internal sealed record ProjectReport(
    string SchemaVersion,
    bool Redacted,
    object Project,
    object[] Runs,
    object[] Artifacts,
    object[] Observations,
    object[] Evidence,
    object[] Entities,
    object[] Relations,
    object[] Findings,
    object[] DiscoveredResources,
    object[] Dns,
    object[] Services,
    object[] Malware,
    object[] ProviderRequests,
    object[] NetworkAttempts,
    object[] ProxySummary,
    object[] Timeline,
    object Coverage);

/// <summary>Exports a complete versioned JSON report.</summary>
public sealed class ReportJsonCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<ReportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var redact = configuration.Security.RedactSensitiveValues && !settings.IncludeSensitive;
        var report = await BuildReportAsync(initializer, store, workspace, redact, cancellationToken).ConfigureAwait(false);
        var path = settings.Output ?? Path.Combine(workspace.ReportsPath, "morsa-report.json");
        output.WriteJsonFile(path, report);
        output.Write(new { output = Path.GetFullPath(path) }, settings.Json);
        return 0;
    }

    internal static async Task<ProjectReport> BuildReportAsync(
        IStoreInitializer initializer,
        IMorsaStore store,
        IWorkspaceContext workspace,
        bool redact,
        CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var runs = await store.Runs.Where(item => item.ProjectId == project.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var runIds = runs.Select(item => item.Id).ToArray();
        var artifacts = await store.Artifacts.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = artifacts.Select(item => item.Id).ToArray();
        var observations = await store.MetadataObservations.Where(item => artifactIds.Contains(item.ArtifactId)).ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var evidence = await store.Evidence.Where(item => artifactIds.Contains(item.ArtifactId)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var entities = await store.Entities.Where(item => item.ProjectId == project.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var timeline = BuildTimeline(runs, artifacts, observations, redact);
        return new ProjectReport(
            BuildInfo.SchemaVersion,
            redact,
            new { project.Id, project.Name, RootPath = redact ? ReportRedaction.Value(project.RootPath) : project.RootPath, project.DefaultMode, project.CreatedAt },
            runs.Cast<object>().ToArray(),
            artifacts.Select(item => ReportRedaction.Artifact(item, redact)).ToArray(),
            observations.Select(item => ReportRedaction.Observation(item, redact)).ToArray(),
            evidence.Select(item => ReportRedaction.Evidence(item, redact)).ToArray(),
            entities.Select(item => ReportRedaction.Entity(item, redact)).ToArray(),
            await store.Relations.Where(item => item.ProjectId == project.Id).Cast<object>().ToArrayAsync(cancellationToken),
            (await store.Findings.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken)).Select(item => ReportRedaction.Finding(item, redact)).ToArray(),
            (await store.DiscoveredResources.Where(item => item.ProjectId == project.Id).ToArrayAsync(cancellationToken)).Select(item => ReportRedaction.Discovered(item, redact)).ToArray(),
            (await store.DnsObservations.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken)).Cast<object>().ToArray(),
            (await store.ServiceObservations.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken)).Select(item => ReportRedaction.Service(item, redact)).ToArray(),
            (await store.MalwareObservations.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken)).Cast<object>().ToArray(),
            (await store.ProviderRequests.Where(item => runIds.Contains(item.RunId)).ToArrayAsync(cancellationToken)).Select(item => ReportRedaction.Provider(item, redact)).ToArray(),
            await store.NetworkAttempts.Where(item => item.RunId.HasValue && runIds.Contains(item.RunId.Value)).Cast<object>().ToArrayAsync(cancellationToken),
            await store.ProxyEndpoints.Select(item => new { item.Protocol, item.Status, item.SuccessCount, item.FailureCount }).Cast<object>()
                .ToArrayAsync(cancellationToken),
            timeline,
            new
            {
                providers_total = await store.ProviderRequests.CountAsync(item => runIds.Contains(item.RunId), cancellationToken),
                providers_failed = await store.ProviderRequests.CountAsync(item => runIds.Contains(item.RunId) && item.Status != "completed", cancellationToken),
                network_attempts = await store.NetworkAttempts.CountAsync(item => item.RunId.HasValue && runIds.Contains(item.RunId.Value), cancellationToken),
                proxy_rotations = await store.NetworkAttempts.CountAsync(item => item.RunId.HasValue && runIds.Contains(item.RunId.Value) && item.RotationReason != null && item.ProxyEndpointId != null, cancellationToken),
                direct_attempts = await store.NetworkAttempts.CountAsync(item => item.RunId.HasValue && runIds.Contains(item.RunId.Value) && item.ProxyEndpointId == null, cancellationToken),
                cooldowns = await store.ProxyEndpoints.CountAsync(item => item.Status == Morsa.Domain.Networking.ProxyStatus.Cooldown, cancellationToken),
            });
    }

    private static object[] BuildTimeline(
        IEnumerable<Morsa.Domain.Runs.Run> runs,
        IEnumerable<Artifact> artifacts,
        IEnumerable<MetadataObservation> observations,
        bool redact)
    {
        var entries = new List<(DateTimeOffset Timestamp, object Value)>();
        foreach (var run in runs)
        {
            entries.Add((run.CreatedAt, new { timestamp = run.CreatedAt, kind = "run_created", source_id = run.Id, value = run.Command }));
            if (run.StartedAt is { } started) entries.Add((started, new { timestamp = started, kind = "run_started", source_id = run.Id, value = run.Command }));
            if (run.FinishedAt is { } finished) entries.Add((finished, new { timestamp = finished, kind = "run_finished", source_id = run.Id, value = run.Status.ToString() }));
        }
        foreach (var artifact in artifacts)
            entries.Add((artifact.AcquiredAt, new { timestamp = artifact.AcquiredAt, kind = "artifact_acquired", source_id = artifact.Id, value = artifact.Sha256 }));
        foreach (var observation in observations.Where(item => item.Category.Contains("date", StringComparison.OrdinalIgnoreCase)))
        {
            if (!DateTimeOffset.TryParse(observation.NormalizedValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp) &&
                !DateTimeOffset.TryParse(observation.OriginalValue, out timestamp)) continue;
            var value = redact ? ReportRedaction.Value(observation.OriginalValue) : observation.OriginalValue;
            entries.Add((timestamp, new { timestamp, kind = "metadata_date", source_id = observation.Id, value }));
        }
        return entries.OrderBy(item => item.Timestamp).ThenBy(item => CliOutput.ToJson(item.Value), StringComparer.Ordinal)
            .Select(item => item.Value).ToArray();
    }
}

/// <summary>Exports interoperable CSV tables with RFC 4180 escaping.</summary>
public sealed class ReportCsvCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<ReportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(settings.Output ?? Path.Combine(workspace.ReportsPath, "csv"));
        Directory.CreateDirectory(root);
        var redact = configuration.Security.RedactSensitiveValues && !settings.IncludeSensitive;
        var runIds = await store.Runs.Where(item => item.ProjectId == project.Id).Select(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var artifactIds = await store.Artifacts.Where(item => runIds.Contains(item.RunId)).Select(item => item.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var entities = await store.Entities.Where(item => item.ProjectId == project.Id).OrderBy(item => item.Type).ThenBy(item => item.NormalizedValue)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var observations = await store.MetadataObservations.Where(item => artifactIds.Contains(item.ArtifactId)).OrderBy(item => item.Category).ThenBy(item => item.NormalizedValue)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        await WriteCsvAsync(Path.Combine(root, "entities.csv"),
            ["id", "type", "value", "normalized_value", "confidence"],
            entities.Select(item => new[] { item.Id.ToString(), item.Type, redact ? ReportRedaction.Value(item.Value) : item.Value, redact ? ReportRedaction.Value(item.NormalizedValue) : item.NormalizedValue, item.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            cancellationToken).ConfigureAwait(false);
        await WriteCsvAsync(Path.Combine(root, "observations.csv"),
            ["id", "artifact_id", "category", "original_value", "normalized_value", "extractor", "location", "confidence"],
            observations.Select(item => new[] { item.Id.ToString(), item.ArtifactId.ToString(), item.Category, redact ? ReportRedaction.Value(item.OriginalValue) : item.OriginalValue, redact ? ReportRedaction.Value(item.NormalizedValue) : item.NormalizedValue, item.Extractor, redact ? ReportRedaction.Value(item.Location ?? string.Empty) : item.Location ?? string.Empty, item.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            cancellationToken).ConfigureAwait(false);
        await WriteCsvAsync(Path.Combine(root, "relations.csv"),
            ["id", "from_entity_id", "to_entity_id", "type", "evidence_id", "confidence"],
            await store.Relations.Where(item => item.ProjectId == project.Id).OrderBy(item => item.FromEntityId).ThenBy(item => item.ToEntityId)
                .Select(item => new[] { item.Id.ToString(), item.FromEntityId.ToString(), item.ToEntityId.ToString(), item.Type, item.EvidenceId.ToString(), item.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                .ToArrayAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        output.Write(new { output = root, files = 3 }, settings.Json);
        return 0;
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<string> header, IEnumerable<string[]> rows, CancellationToken cancellationToken)
    {
        static string Escape(string value)
        {
            // Neutralize spreadsheet formulas while preserving the visible forensic value.
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
            return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        }
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync(string.Join(',', header.Select(Escape))).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', row.Select(Escape))).ConfigureAwait(false);
        }
    }
}

public sealed class ReportBundleSettings : ReportSettings
{
    [CommandOption("--redact")]
    public bool Redact { get; init; }
}

/// <summary>Creates a deterministic ZIP with report, evidence manifest and optional immutable artifacts.</summary>
public sealed class ReportBundleCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<ReportBundleSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportBundleSettings settings, CancellationToken cancellationToken)
    {
        var redact = settings.Redact || (configuration.Security.RedactSensitiveValues && !settings.IncludeSensitive);
        var report = await ReportJsonCommand.BuildReportAsync(initializer, store, workspace, redact, cancellationToken).ConfigureAwait(false);
        var path = Path.GetFullPath(settings.Output ?? Path.Combine(workspace.ReportsPath, "morsa-evidence.zip"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "report.json", CliOutput.ToJson(report), cancellationToken).ConfigureAwait(false);
        var artifacts = await store.Artifacts.OrderBy(item => item.Sha256).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var manifest = artifacts.Select(item => new { item.Id, item.Sha256, item.Size, item.MimeType, included = !redact }).ToArray();
        await WriteEntryAsync(archive, "evidence-manifest.json", CliOutput.ToJson(new { schema_version = "1", redacted = redact, artifacts = manifest }), cancellationToken).ConfigureAwait(false);
        if (!redact)
        {
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Artifact hash is invalid and cannot be used as a bundle entry name.");
                var storedPath = Path.GetFullPath(artifact.StoredPath);
                var artifactRoot = Path.GetFullPath(workspace.ArtifactsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!storedPath.StartsWith(artifactRoot, comparison) ||
                    (File.GetAttributes(storedPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Artifact path escapes content-addressable storage or is a symbolic link.");
                var entry = archive.CreateEntry($"artifacts/{artifact.Sha256}", CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var source = File.OpenRead(storedPath);
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        output.Write(new { output = path, artifacts = redact ? 0 : artifacts.Length, redacted = redact }, settings.Json);
        return 0;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Produces a standalone encoded HTML summary with no active scripts.</summary>
public sealed class ReportHtmlCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<ReportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReportSettings settings, CancellationToken cancellationToken)
    {
        var redact = configuration.Security.RedactSensitiveValues && !settings.IncludeSensitive;
        var report = await ReportJsonCommand.BuildReportAsync(initializer, store, workspace, redact, cancellationToken).ConfigureAwait(false);
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

internal static class ReportRedaction
{
    private static readonly HashSet<string> SensitiveCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "author", "last_saved_by", "email", "username", "hostname", "server", "domain", "url", "path",
        "unc_path", "gps", "password", "credential", "credential_indicator", "manager",
    };

    public static string Value(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
        return $"[redacted:{digest}]";
    }

    public static object Artifact(Artifact item, bool redact) => !redact ? item : new
    {
        item.Id,
        item.RunId,
        SourceUri = RedactUri(item.SourceUri),
        OriginalPath = item.OriginalPath is null ? null : Value(item.OriginalPath),
        item.Sha256,
        item.Size,
        item.MimeType,
        item.Kind,
        item.AcquiredAt,
    };

    public static object Observation(MetadataObservation item, bool redact)
    {
        var sensitive = redact && SensitiveCategories.Contains(item.Category);
        return !sensitive ? item : new
        {
            item.Id,
            item.ArtifactId,
            item.Category,
            OriginalValue = Value(item.OriginalValue),
            NormalizedValue = Value(item.NormalizedValue),
            item.Extractor,
            item.ExtractorVersion,
            Location = item.Location is null ? null : Value(item.Location),
            item.Confidence,
            item.ObservedAt,
        };
    }

    public static object Evidence(Evidence item, bool redact) => !redact ? item : new
    {
        item.Id,
        item.ArtifactId,
        item.ObservationId,
        item.Source,
        Value = Value(item.Value),
        Location = item.Location is null ? null : Value(item.Location),
        item.ArtifactSha256,
        item.CollectedAt,
    };

    public static object Entity(EntityNode item, bool redact)
    {
        if (!redact) return item;
        if (item.Type.Equals("artifact", StringComparison.OrdinalIgnoreCase))
        {
            // Artifact entities use Value for the source path and NormalizedValue for the non-sensitive SHA-256 identity.
            return new
            {
                item.Id,
                item.ProjectId,
                item.Type,
                Value = Value(item.Value),
                item.NormalizedValue,
                item.Confidence,
            };
        }

        if (!SensitiveCategories.Contains(item.Type)) return item;
        return new
        {
            item.Id,
            item.ProjectId,
            item.Type,
            Value = Value(item.Value),
            NormalizedValue = Value(item.NormalizedValue),
            item.Confidence,
        };
    }

    public static object Finding(Finding item, bool redact) => !redact || !item.Sensitive ? item : new
    {
        item.Id,
        item.RunId,
        item.ArtifactId,
        item.RuleId,
        item.Title,
        Description = Value(item.Description),
        item.Severity,
        item.Confidence,
        item.Sensitive,
    };

    public static object Discovered(Morsa.Domain.Discovery.DiscoveredResource item, bool redact) => !redact ? item : new
    {
        item.Id,
        item.ProjectId,
        item.RunId,
        Url = RedactUri(item.Url),
        CanonicalUrl = RedactUri(item.CanonicalUrl),
        item.ProviderId,
        Query = Value(item.Query),
        item.Title,
        item.Snippet,
        item.Status,
        item.LastError,
        item.DiscoveredAt,
    };

    public static object Service(Morsa.Domain.Recon.ServiceObservation item, bool redact) => !redact ? item : new
    {
        item.Id,
        item.RunId,
        Host = Value(item.Host),
        item.Port,
        item.Protocol,
        Banner = item.Banner is null ? null : Value(item.Banner),
        item.TlsSubject,
        item.TlsIssuer,
        item.Technology,
        item.ObservedAt,
    };

    public static object Provider(Morsa.Domain.Discovery.ProviderRequest item, bool redact) => !redact ? item : new
    {
        item.Id,
        item.RunId,
        item.ProviderId,
        Query = Value(item.Query),
        item.Status,
        item.AttemptCount,
        item.LastCursor,
        item.NextRetryAt,
        item.CoverageTagsJson,
        item.LastError,
    };

    private static string? RedactUri(string? value)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value is null ? null : Value(value);
        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }
}
