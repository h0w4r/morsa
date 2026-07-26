using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Cli.Runtime;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class IngestFileSettings : WorkspaceSettings
{
    [CommandArgument(0, "<FILE>")]
    public required string File { get; init; }

    [CommandOption("--max-mb <MB>")]
    public int MaxMb { get; init; } = 100;
}

/// <summary>Streams one local file into content-addressable quarantine.</summary>
public sealed class IngestFileCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    IArtifactStorage storage,
    RunCoordinator runs,
    CliOutput output) : AsyncCommand<IngestFileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, IngestFileSettings settings, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(settings.File);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Artifact does not exist.", path);
        }

        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "ingest file", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        await using var source = File.OpenRead(path);
        var stored = await storage.StoreAsync(source, Path.GetFileName(path), settings.MaxMb * 1024L * 1024L, cancellationToken)
            .ConfigureAwait(false);
        var artifact = new Artifact
        {
            RunId = run.Id,
            OriginalPath = path,
            StoredPath = stored.Path,
            Sha256 = stored.Sha256,
            Size = stored.Size,
            Kind = stored.Kind,
            MimeType = stored.MimeType,
        };
        store.Add(artifact);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(artifact, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class IngestDirectorySettings : WorkspaceSettings
{
    [CommandArgument(0, "<DIRECTORY>")]
    public required string Directory { get; init; }

    [CommandOption("--recursive")]
    public bool Recursive { get; init; }

    [CommandOption("--max-files <COUNT>")]
    public int MaxFiles { get; init; } = 10_000;
}

/// <summary>Ingests a bounded directory enumeration without following reparse points.</summary>
public sealed class IngestDirectoryCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    IArtifactStorage storage,
    RunCoordinator runs,
    CliOutput output) : AsyncCommand<IngestDirectorySettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, IngestDirectorySettings settings, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(settings.Directory);
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "ingest directory", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var files = System.IO.Directory.EnumerateFiles(
                root,
                "*",
                settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Take(settings.MaxFiles)
            .ToArray();
        var artifacts = new List<Artifact>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            await using var source = File.OpenRead(path);
            var stored = await storage.StoreAsync(source, Path.GetFileName(path), 100 * 1024L * 1024L, cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(new Artifact
            {
                RunId = run.Id,
                OriginalPath = path,
                StoredPath = stored.Path,
                Sha256 = stored.Sha256,
                Size = stored.Size,
                Kind = stored.Kind,
                MimeType = stored.MimeType,
            });
        }

        store.AddRange(artifacts);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(new { ingested = artifacts.Count, run_id = run.Id }, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

/// <summary>Runs the selected safe extractor for every not-yet-analyzed artifact.</summary>
public sealed class AnalyzeAllCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    ArtifactAnalysisService analysis,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var analyzedIds = store.MetadataObservations.Select(item => item.ArtifactId).Distinct().ToHashSet();
        var artifacts = await store.Artifacts.Where(item => !analyzedIds.Contains(item.Id)).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var observations = 0;
        var diagnostics = new List<object>();
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await analysis.AnalyzeAsync(artifact, new ExtractionOptions(), cancellationToken).ConfigureAwait(false);
            observations += result.Observations.Count;
            diagnostics.AddRange(result.Diagnostics.Select(item => new { artifact_id = artifact.Id, item.Code, item.Message, item.IsError }));
        }

        output.Write(new { analyzed = artifacts.Count, observations, diagnostics }, settings.Json);
        return diagnostics.Any() ? 5 : 0;
    }
}

/// <summary>Normalizes extracted values into stable project entities.</summary>
public sealed class CorrelateCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CorrelationService correlation,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var added = await correlation.CorrelateAsync(project.Id, cancellationToken).ConfigureAwait(false);
        output.Write(new { entities_added = added, entities_total = store.Entities.Count() }, settings.Json);
        return 0;
    }
}


