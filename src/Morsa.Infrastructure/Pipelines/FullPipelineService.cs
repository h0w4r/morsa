using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Acquisition;
using Morsa.Infrastructure.Discovery;

namespace Morsa.Infrastructure.Pipelines;

/// <summary>Runs the resumable end-to-end document pipeline using one durable run.</summary>
public sealed class FullPipelineService(
    IMorsaStore store,
    RunCoordinator runs,
    DiscoveryService discovery,
    AcquisitionService acquisition,
    ArtifactAnalysisService analysis,
    CorrelationService correlation)
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
        var selectedProviders = providers.ToList();
        if (activeCrawl && !selectedProviders.Contains("direct-crawler", StringComparer.OrdinalIgnoreCase))
        {
            selectedProviders.Add("direct-crawler");
        }

        var query = new SearchQuery(target, types, MaxResults: 500);
        var discovered = await discovery.DiscoverAsync(
            projectId,
            run.Id,
            query,
            new SearchExecutionContext(run.Id, null, run.Id.ToString("N"), proxyPool, 500),
            selectedProviders,
            cancellationToken).ConfigureAwait(false);
        var fetched = await acquisition.FetchPendingAsync(projectId, run.Id, proxyPool, 100 * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        var artifacts = await store.Artifacts.Where(item => item.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var observationCount = 0;
        var parserFailures = 0;
        foreach (var artifact in artifacts)
        {
            var result = await analysis.AnalyzeAsync(artifact, new ExtractionOptions(), cancellationToken).ConfigureAwait(false);
            observationCount += result.Observations.Count;
            parserFailures += result.Diagnostics.Count(item => item.IsError);
        }

        var entities = await correlation.CorrelateAsync(projectId, cancellationToken).ConfigureAwait(false);
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

    public async Task<PipelineResult> ResumeAsync(
        Guid projectId,
        string? proxyPool,
        CancellationToken cancellationToken)
    {
        var run = await runs.StartAsync(projectId, "run resume", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var fetched = await acquisition.FetchPendingAsync(projectId, run.Id, proxyPool, 100 * 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        var artifacts = await store.Artifacts.Where(item => !store.MetadataObservations.Any(observation => observation.ArtifactId == item.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var observations = 0;
        var parserFailures = 0;
        foreach (var artifact in artifacts)
        {
            var result = await analysis.AnalyzeAsync(artifact, new ExtractionOptions(), cancellationToken).ConfigureAwait(false);
            observations += result.Observations.Count;
            parserFailures += result.Diagnostics.Count(item => item.IsError);
        }

        var entities = await correlation.CorrelateAsync(projectId, cancellationToken).ConfigureAwait(false);
        var partial = fetched.Failed > 0 || parserFailures > 0;
        await runs.CompleteAsync(run, partial ? ExecutionStatus.PartiallyFailed : ExecutionStatus.Completed, partial ? "partial_provider_failure" : "complete", cancellationToken)
            .ConfigureAwait(false);
        return new PipelineResult(run.Id, 0, fetched.Downloaded, fetched.Failed, artifacts.Count, observations, entities, [], partial ? "partial_provider_failure" : "complete");
    }
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

