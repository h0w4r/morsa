using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Domain.Discovery;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Discovery;

namespace Morsa.Mcp.Tools;

/// <summary>Artifact ingestion, discovery, acquisition and extraction tools.</summary>
[McpServerToolType]
public static class ArtifactDiscoveryTools
{
    [McpServerTool(Name = "morsa_ingest_file")]
    [Description("Ingests one workspace-confined file into content-addressed storage.")]
    public static async Task<object> IngestFile(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("File path; relative paths are resolved below the workspace root.")] string file,
        [Description("Maximum accepted file size in MiB.")] int maximum_mb = 100,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var input = WorkspacePathPolicy.ResolveInputFile(context.Workspace, file);
        var maximumBytes = checked((long)Math.Clamp(maximum_mb, 1, 2_048) * 1024 * 1024);
        var execution = await context.ExecuteRunAsync(
            "mcp ingest file",
            ActivityMode.Passive,
            async (run, token) =>
            {
                await using var source = new FileStream(
                    input,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var stored = await context.GetRequiredService<IArtifactStorage>()
                    .StoreAsync(source, Path.GetFileName(input), maximumBytes, token).ConfigureAwait(false);
                var artifact = new Artifact
                {
                    RunId = run.Id,
                    OriginalPath = input,
                    StoredPath = stored.Path,
                    Sha256 = stored.Sha256,
                    Size = stored.Size,
                    Kind = stored.Kind,
                    MimeType = stored.MimeType,
                };
                context.Store.Add(artifact);
                await context.Store.SaveChangesAsync(token).ConfigureAwait(false);
                return artifact;
            },
            cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            run_id = execution.Run.Id,
            coverage = execution.Run.CoverageStatus,
            artifact = execution.Result,
        };
    }

    [McpServerTool(Name = "morsa_ingest_url")]
    [Description("Downloads one in-scope HTTP(S) artifact with redirect and byte budgets.")]
    public static async Task<object> IngestUrl(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Absolute in-scope HTTP or HTTPS URL.")] string url,
        [Description("Optional configured proxy pool name.")] string? proxy_pool = null,
        [Description("Maximum response size in MiB.")] int maximum_mb = 100,
        [Description("Maximum number of redirects.")] int maximum_redirects = 5,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("URL must be an absolute HTTP or HTTPS URI.", nameof(url));
        }

        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var execution = await context.ExecuteRunAsync(
            "mcp ingest url",
            ActivityMode.Passive,
            async (run, token) =>
            {
                var canonical = DiscoveryUtilities.Canonicalize(uri.AbsoluteUri);
                var resource = await context.Store.DiscoveredResources.SingleOrDefaultAsync(
                    item => item.ProjectId == context.Project.Id && item.CanonicalUrl == canonical,
                    token).ConfigureAwait(false);
                if (resource is null)
                {
                    resource = new DiscoveredResource
                    {
                        ProjectId = context.Project.Id,
                        RunId = run.Id,
                        Url = uri.AbsoluteUri,
                        CanonicalUrl = canonical,
                        ProviderId = "mcp",
                        Query = uri.IdnHost,
                    };
                    context.Store.Add(resource);
                    await context.Store.SaveChangesAsync(token).ConfigureAwait(false);
                }
                else
                {
                    // Explicit URL ingestion is a new user request even if discovery saw it earlier.
                    resource.Status = "pending";
                    resource.LastError = null;
                }

                return await context.GetRequiredService<AcquisitionService>().FetchAsync(
                    context.Project.Id,
                    run.Id,
                    resource,
                    proxy_pool,
                    checked(Math.Clamp(maximum_mb, 1, 2_047) * 1024 * 1024),
                    Math.Clamp(maximum_redirects, 0, 20),
                    allowPrivateNetworks: false,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            run_id = execution.Run.Id,
            coverage = execution.Run.CoverageStatus,
            artifact = execution.Result,
        };
    }

    [McpServerTool(Name = "morsa_discover_documents")]
    [Description("Discovers document URLs through selected providers and persists partial coverage.")]
    public static async Task<object> DiscoverDocuments(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Domain or target search expression.")] string target,
        [Description("Document extensions to discover.")] string[]? types = null,
        [Description("Provider IDs such as searxng, duckduckgo or commoncrawl.")] string[]? providers = null,
        [Description("Optional configured proxy pool name.")] string? proxy_pool = null,
        [Description("Maximum results per discovery request.")] int maximum_results = 100,
        [Description("Enables the explicitly active direct crawler provider.")] bool active_crawl = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var coordinator = context.GetRequiredService<RunCoordinator>();
        var mode = active_crawl ? ActivityMode.Active : ActivityMode.Passive;
        await NetworkToolPolicy.RequireScopeAsync(
            context,
            target.Trim().TrimEnd('.'),
            443,
            mode,
            cancellationToken).ConfigureAwait(false);
        var run = await coordinator.StartAsync(context.Project.Id, "mcp discover documents", mode, cancellationToken).ConfigureAwait(false);
        try
        {
            var selectedProviders = (providers is { Length: > 0 } ? providers : ["searxng", "duckduckgo", "commoncrawl"])
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (active_crawl && !selectedProviders.Contains("direct-crawler", StringComparer.OrdinalIgnoreCase))
            {
                selectedProviders.Add("direct-crawler");
            }

            var selectedTypes = types is { Length: > 0 }
                ? types.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToArray()
                : new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "svg" };
            var limit = Math.Clamp(maximum_results, 1, 5_000);
            var query = new SearchQuery(target.Trim(), selectedTypes, MaxResults: limit);
            var result = await context.GetRequiredService<DiscoveryService>().DiscoverAsync(
                context.Project.Id,
                run.Id,
                query,
                new SearchExecutionContext(run.Id, null, run.Id.ToString("N"), proxy_pool, checked(limit * Math.Max(1, selectedTypes.Length)), context.Project.Id),
                selectedProviders,
                cancellationToken).ConfigureAwait(false);
            var status = result.FailedProviders.Count == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed;
            var coverage = result.FailedProviders.Count == 0 ? "complete" : "partial_provider_failure";
            await coordinator.CompleteAsync(run, status, coverage, cancellationToken).ConfigureAwait(false);
            return new
            {
                schema_version = McpContract.SchemaVersion,
                run_id = run.Id,
                discovered = result.Added,
                failed_providers = result.FailedProviders,
                coverage,
            };
        }
        catch (OperationCanceledException)
        {
            await CompleteBestEffortAsync(coordinator, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteBestEffortAsync(coordinator, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }

    [McpServerTool(Name = "morsa_fetch_pending")]
    [Description("Fetches pending discovered resources using scope checks and bounded proxy rotation.")]
    public static async Task<object> FetchPending(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Optional configured proxy pool name.")] string? proxy_pool = null,
        [Description("Maximum response size per artifact in MiB.")] int maximum_mb = 100,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var coordinator = context.GetRequiredService<RunCoordinator>();
        var run = await coordinator.StartAsync(context.Project.Id, "mcp fetch pending", ActivityMode.Passive, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var result = await context.GetRequiredService<AcquisitionService>().FetchPendingAsync(
                context.Project.Id,
                run.Id,
                proxy_pool,
                checked(Math.Clamp(maximum_mb, 1, 2_047) * 1024 * 1024),
                cancellationToken).ConfigureAwait(false);
            var partial = result.Failed > 0;
            await coordinator.CompleteAsync(
                run,
                partial ? ExecutionStatus.PartiallyFailed : ExecutionStatus.Completed,
                partial ? "partial_download_failure" : "complete",
                cancellationToken).ConfigureAwait(false);
            return new
            {
                schema_version = McpContract.SchemaVersion,
                run_id = run.Id,
                downloaded = result.Downloaded,
                failed = result.Failed,
                coverage = partial ? "partial_download_failure" : "complete",
            };
        }
        catch (OperationCanceledException)
        {
            await CompleteBestEffortAsync(coordinator, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteBestEffortAsync(coordinator, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }

    [McpServerTool(Name = "morsa_analyze")]
    [Description("Extracts metadata from one artifact or all pending artifacts in the workspace.")]
    public static async Task<object> Analyze(
        [Description("Initialized Morsa workspace directory.")] string path,
        [Description("Optional artifact UUID; omitted means all pending artifacts.")] Guid? artifact_id = null,
        [Description("Reanalyzes artifacts that already contain metadata observations.")] bool include_analyzed = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var coordinator = context.GetRequiredService<RunCoordinator>();
        var execution = await coordinator.ExecuteAsync(
            context.Project.Id,
            "mcp analyze",
            ActivityMode.Passive,
            async (_, token) =>
            {
                var runIds = context.Store.Runs.Where(run => run.ProjectId == context.Project.Id).Select(run => run.Id);
                var query = context.Store.Artifacts.Where(artifact => runIds.Contains(artifact.RunId));
                if (artifact_id is not null) query = query.Where(artifact => artifact.Id == artifact_id.Value);
                if (!include_analyzed) query = query.Where(artifact => !context.Store.MetadataObservations.Any(item => item.ArtifactId == artifact.Id));
                var artifacts = await query.OrderBy(artifact => artifact.Id).ToArrayAsync(token).ConfigureAwait(false);
                if (artifact_id is not null && artifacts.Length == 0)
                {
                    throw new KeyNotFoundException("The requested artifact does not exist in this project or is already analyzed.");
                }

                var analyzer = context.GetRequiredService<ArtifactAnalysisService>();
                var observationCount = 0;
                var findingCount = 0;
                var hasParserErrors = false;
                var diagnostics = new List<object>();
                foreach (var artifact in artifacts)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await analyzer.AnalyzeAsync(artifact, new ExtractionOptions(), token).ConfigureAwait(false);
                    observationCount += result.Observations.Count;
                    findingCount += result.Findings.Count;
                    hasParserErrors |= result.Diagnostics.Any(item => item.IsError);
                    diagnostics.AddRange(result.Diagnostics.Select(item => new
                    {
                        artifact_id = artifact.Id,
                        code = item.Code,
                        message = item.Message,
                        is_error = item.IsError,
                    }));
                }

                return new McpAnalyzeResult(artifacts.Length, observationCount, findingCount, diagnostics, hasParserErrors);
            },
            cancellationToken,
            result => result.HasParserErrors
                ? (ExecutionStatus.PartiallyFailed, "partial_parser_failure")
                : (ExecutionStatus.Completed, "complete")).ConfigureAwait(false);

        return new
        {
            schema_version = McpContract.SchemaVersion,
            run_id = execution.Run.Id,
            analyzed = execution.Result.Analyzed,
            observations = execution.Result.Observations,
            findings = execution.Result.Findings,
            diagnostics = execution.Result.Diagnostics,
            coverage = execution.Run.CoverageStatus,
        };
    }

    [McpServerTool(Name = "morsa_correlate")]
    [Description("Builds normalized evidence-backed entities and relations for the project.")]
    public static async Task<object> Correlate(
        [Description("Initialized Morsa workspace directory.")] string path,
        CancellationToken cancellationToken = default)
    {
        await using var context = await WorkspaceToolContext.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var execution = await context.GetRequiredService<RunCoordinator>().ExecuteAsync(
            context.Project.Id,
            "mcp correlate",
            ActivityMode.Passive,
            (_, token) => context.GetRequiredService<CorrelationService>().CorrelateAsync(context.Project.Id, token),
            cancellationToken).ConfigureAwait(false);
        var total = await context.Store.Entities.CountAsync(item => item.ProjectId == context.Project.Id, cancellationToken).ConfigureAwait(false);
        return new
        {
            schema_version = McpContract.SchemaVersion,
            run_id = execution.Run.Id,
            entities_added = execution.Result,
            entities_total = total,
            coverage = execution.Run.CoverageStatus,
        };
    }

    private static async Task CompleteBestEffortAsync(
        RunCoordinator coordinator,
        Morsa.Domain.Runs.Run run,
        ExecutionStatus status,
        string coverage)
    {
        try
        {
            await coordinator.CompleteAsync(run, status, coverage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original exception remains the authoritative MCP error.
        }
    }

    private sealed record McpAnalyzeResult(
        int Analyzed,
        int Observations,
        int Findings,
        IReadOnlyList<object> Diagnostics,
        bool HasParserErrors);
}
