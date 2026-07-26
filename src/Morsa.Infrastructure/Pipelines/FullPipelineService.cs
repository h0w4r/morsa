using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Discovery;

namespace Morsa.Infrastructure.Pipelines;

/// <summary>Runs the resumable end-to-end document pipeline using one durable run.</summary>
public sealed class FullPipelineService(
    IMorsaStore store,
    RunCoordinator runs,
    DiscoveryService discovery,
    AcquisitionService acquisition,
    ArtifactAnalysisService analysis,
    CorrelationService correlation,
    MorsaConfiguration configuration)
{
    public async Task<PipelineResult> RunAsync(
        Guid projectId,
        string target,
        IReadOnlyCollection<string> types,
        IReadOnlyCollection<string> providers,
        string? proxyPool,
        bool activeCrawl,
        CancellationToken cancellationToken)
    {
        var run = await runs.StartAsync(projectId, "run full", activeCrawl ? ActivityMode.Active : ActivityMode.Passive, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var selectedProviders = providers.ToList();
            if (activeCrawl && !selectedProviders.Contains("direct-crawler", StringComparer.OrdinalIgnoreCase))
            {
                selectedProviders.Add("direct-crawler");
            }

            var queryBudget = Math.Clamp(configuration.Network.QueryBudget, 1, 100_000);
            var query = new SearchQuery(target, types, MaxResults: queryBudget);
            var discoveryTask = await runs.GetOrCreateTaskAsync(run, "discovery", $"discovery:{target}:{string.Join(',', types)}", JsonSerializer.Serialize(query), cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(discoveryTask, cancellationToken).ConfigureAwait(false);
            var discovered = await discovery.DiscoverAsync(
                projectId,
                run.Id,
                query,
                new SearchExecutionContext(run.Id, null, run.Id.ToString("N"), proxyPool, queryBudget, projectId),
                selectedProviders,
                cancellationToken).ConfigureAwait(false);
            await runs.CompleteTaskAsync(discoveryTask,
                discovered.FailedProviders.Count == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed,
                discovered.FailedProviders.Count == 0 ? null : "PROVIDER_PARTIAL_FAILURE",
                discovered.FailedProviders.Count == 0 ? null : string.Join(',', discovered.FailedProviders),
                cancellationToken).ConfigureAwait(false);
            var fetchTask = await runs.GetOrCreateTaskAsync(run, "acquisition", $"fetch:{target}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(fetchTask, cancellationToken).ConfigureAwait(false);
            var fetched = await acquisition.FetchPendingAsync(
                    projectId,
                    run.Id,
                    proxyPool,
                    checked(configuration.Artifacts.MaxDownloadMb * 1024 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
            await runs.CompleteTaskAsync(fetchTask, fetched.Failed == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed,
                fetched.Failed == 0 ? null : "DOWNLOAD_PARTIAL_FAILURE", fetched.Failed == 0 ? null : $"{fetched.Failed} downloads failed.", cancellationToken).ConfigureAwait(false);
            var parserTask = await runs.GetOrCreateTaskAsync(run, "metadata", $"metadata:{run.Id:N}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(parserTask, cancellationToken).ConfigureAwait(false);
            var artifacts = await store.Artifacts.Where(item => item.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var observationCount = 0;
            var parserFailures = 0;
            foreach (var artifact in artifacts)
            {
                var result = await analysis.AnalyzeAsync(artifact, CreateExtractionOptions(), cancellationToken).ConfigureAwait(false);
                observationCount += result.Observations.Count;
                parserFailures += result.Diagnostics.Count(item => item.IsError);
            }
            await runs.CompleteTaskAsync(parserTask, parserFailures == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed,
                parserFailures == 0 ? null : "PARSER_PARTIAL_FAILURE", parserFailures == 0 ? null : $"{parserFailures} parser diagnostics were errors.", cancellationToken).ConfigureAwait(false);

            var correlationTask = await runs.GetOrCreateTaskAsync(run, "correlation", $"correlation:{projectId:N}:{run.Id:N}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(correlationTask, cancellationToken).ConfigureAwait(false);
            var entities = await correlation.CorrelateAsync(projectId, cancellationToken).ConfigureAwait(false);
            await runs.CompleteTaskAsync(correlationTask, ExecutionStatus.Completed, null, null, cancellationToken).ConfigureAwait(false);
            var partial = discovered.FailedProviders.Count > 0 || fetched.Failed > 0 || parserFailures > 0;
            await runs.CompleteAsync(
                run,
                partial ? ExecutionStatus.PartiallyFailed : ExecutionStatus.Completed,
                partial ? "partial_provider_failure" : "complete",
                cancellationToken).ConfigureAwait(false);
            return new PipelineResult(
                run.Id,
                discovered.Added,
                fetched.Downloaded,
                fetched.Failed,
                artifacts.Count,
                observationCount,
                entities,
                discovered.FailedProviders,
                partial ? "partial_provider_failure" : "complete");
        }
        catch (OperationCanceledException)
        {
            await CompleteBestEffortAsync(run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteBestEffortAsync(run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PipelineResult> ResumeAsync(
        Guid projectId,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var run = await runs.StartAsync(projectId, "run resume", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        try
        {
            // A previous bounded attempt leaves retryable resources as failed; resume must explicitly requeue them.
            await acquisition.RequeueFailedAsync(projectId, cancellationToken).ConfigureAwait(false);
            var fetchTask = await runs.GetOrCreateTaskAsync(run, "acquisition", $"resume-fetch:{projectId:N}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(fetchTask, cancellationToken).ConfigureAwait(false);
            var fetched = await acquisition.FetchPendingAsync(
                    projectId,
                    run.Id,
                    proxyPool,
                    checked(configuration.Artifacts.MaxDownloadMb * 1024 * 1024),
                    cancellationToken)
                .ConfigureAwait(false);
            await runs.CompleteTaskAsync(fetchTask, fetched.Failed == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed,
                fetched.Failed == 0 ? null : "DOWNLOAD_PARTIAL_FAILURE", fetched.Failed == 0 ? null : $"{fetched.Failed} downloads failed.", cancellationToken).ConfigureAwait(false);
            var artifacts = await store.Artifacts.Where(item => !store.MetadataObservations.Any(observation => observation.ArtifactId == item.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var observations = 0;
            var parserFailures = 0;
            var parserTask = await runs.GetOrCreateTaskAsync(run, "metadata", $"resume-metadata:{projectId:N}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(parserTask, cancellationToken).ConfigureAwait(false);
            foreach (var artifact in artifacts)
            {
                var result = await analysis.AnalyzeAsync(artifact, CreateExtractionOptions(), cancellationToken).ConfigureAwait(false);
                observations += result.Observations.Count;
                parserFailures += result.Diagnostics.Count(item => item.IsError);
            }
            await runs.CompleteTaskAsync(parserTask, parserFailures == 0 ? ExecutionStatus.Completed : ExecutionStatus.PartiallyFailed,
                parserFailures == 0 ? null : "PARSER_PARTIAL_FAILURE", parserFailures == 0 ? null : $"{parserFailures} parser diagnostics were errors.", cancellationToken).ConfigureAwait(false);

            var correlationTask = await runs.GetOrCreateTaskAsync(run, "correlation", $"resume-correlation:{projectId:N}", null, cancellationToken).ConfigureAwait(false);
            await runs.BeginTaskAsync(correlationTask, cancellationToken).ConfigureAwait(false);
            var entities = await correlation.CorrelateAsync(projectId, cancellationToken).ConfigureAwait(false);
            await runs.CompleteTaskAsync(correlationTask, ExecutionStatus.Completed, null, null, cancellationToken).ConfigureAwait(false);
            var partial = fetched.Failed > 0 || parserFailures > 0;
            await runs.CompleteAsync(run, partial ? ExecutionStatus.PartiallyFailed : ExecutionStatus.Completed, partial ? "partial_provider_failure" : "complete", cancellationToken)
                .ConfigureAwait(false);
            return new PipelineResult(run.Id, 0, fetched.Downloaded, fetched.Failed, artifacts.Count, observations, entities, [], partial ? "partial_provider_failure" : "complete");
        }
        catch (OperationCanceledException)
        {
            await CompleteBestEffortAsync(run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await CompleteBestEffortAsync(run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteBestEffortAsync(Morsa.Domain.Runs.Run run, ExecutionStatus status, string coverage)
    {
        try
        {
            await runs.CompleteAsync(run, status, coverage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original pipeline failure if journal closure also fails.
        }
    }

    private ExtractionOptions CreateExtractionOptions() => new(
        MaxBytes: checked(configuration.Artifacts.MaxDownloadMb * 1024L * 1024L),
        MaxUncompressedBytes: checked(configuration.Artifacts.MaxUncompressedMb * 1024L * 1024L),
        Timeout: TimeSpan.FromSeconds(configuration.Network.TimeoutSeconds));
}

public sealed record PipelineResult(
    Guid RunId,
    int Discovered,
    int Downloaded,
    int DownloadFailures,
    int Analyzed,
    int Observations,
    int EntitiesAdded,
    IReadOnlyList<string> FailedProviders,
    string Coverage);
