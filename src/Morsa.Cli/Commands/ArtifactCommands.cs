using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Cli.Runtime;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Configuration;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class IngestFileSettings : WorkspaceSettings
{
    [CommandArgument(0, "<FILE>")]
    public required string File { get; init; }

    [CommandOption("--max-mb <MB>")]
    public int? MaxMb { get; init; }
}

/// <summary>Streams one local file into content-addressable quarantine.</summary>
public sealed class IngestFileCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    IArtifactStorage storage,
    RunCoordinator runs,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<IngestFileSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, IngestFileSettings settings, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(settings.File);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Artifact does not exist.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Artifact input must not be a symbolic link or reparse point.");

        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "ingest file",
            ActivityMode.Passive,
            async (run, token) =>
            {
                await using var source = File.OpenRead(path);
                var stored = await storage.StoreAsync(
                        source,
                        Path.GetFileName(path),
                        CommandHelpers.ToLongByteBudget(settings.MaxMb ?? configuration.Artifacts.MaxDownloadMb),
                        token)
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
                await store.SaveChangesAsync(token).ConfigureAwait(false);
                return artifact;
            },
            cancellationToken).ConfigureAwait(false);
        output.Write(execution.Result, settings.Json, execution.Run.Id.ToString(), execution.Run.CoverageStatus);
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

    [CommandOption("--max-mb <MB>")]
    public int? MaxMb { get; init; }
}

/// <summary>Ingests a bounded directory enumeration without following reparse points.</summary>
public sealed class IngestDirectoryCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    IArtifactStorage storage,
    RunCoordinator runs,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<IngestDirectorySettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, IngestDirectorySettings settings, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(settings.Directory);
        if (!System.IO.Directory.Exists(root)) throw new DirectoryNotFoundException($"Artifact directory does not exist: {root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Artifact directory must not be a symbolic link or reparse point.");
        var maximumFiles = Math.Clamp(settings.MaxFiles, 1, 1_000_000);
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = settings.Recursive,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var files = System.IO.Directory.EnumerateFiles(root, "*", enumeration)
            .Take(maximumFiles)
            .ToArray();
        var maximumBytes = CommandHelpers.ToLongByteBudget(settings.MaxMb ?? configuration.Artifacts.MaxDownloadMb);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "ingest directory",
            ActivityMode.Passive,
            async (run, token) =>
            {
                var artifacts = new List<Artifact>();
                foreach (var path in files)
                {
                    token.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    await using var source = File.OpenRead(path);
                    var stored = await storage.StoreAsync(source, Path.GetFileName(path), maximumBytes, token)
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
                await store.SaveChangesAsync(token).ConfigureAwait(false);
                return artifacts.Count;
            },
            cancellationToken).ConfigureAwait(false);
        output.Write(new { ingested = execution.Result, run_id = execution.Run.Id }, settings.Json, execution.Run.Id.ToString(), execution.Run.CoverageStatus);
        return 0;
    }
}

/// <summary>Runs the selected safe extractor for every not-yet-analyzed artifact.</summary>
public sealed class AnalyzeAllCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    ArtifactAnalysisService analysis,
    RunCoordinator runs,
    MorsaConfiguration configuration,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "analyze all",
            ActivityMode.Passive,
            async (_, token) =>
            {
                var analyzedIds = store.MetadataObservations.Select(item => item.ArtifactId).Distinct().ToHashSet();
                var artifacts = await store.Artifacts.Where(item => !analyzedIds.Contains(item.Id)).ToListAsync(token)
                    .ConfigureAwait(false);
                var observations = 0;
                var hasErrors = false;
                var diagnostics = new List<object>();
                foreach (var artifact in artifacts)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await analysis.AnalyzeAsync(
                            artifact,
                            new ExtractionOptions(
                                MaxBytes: CommandHelpers.ToLongByteBudget(configuration.Artifacts.MaxDownloadMb),
                                MaxUncompressedBytes: CommandHelpers.ToLongByteBudget(configuration.Artifacts.MaxUncompressedMb),
                                Timeout: TimeSpan.FromSeconds(configuration.Network.TimeoutSeconds)),
                            token)
                        .ConfigureAwait(false);
                    observations += result.Observations.Count;
                    hasErrors |= result.Diagnostics.Any(item => item.IsError);
                    diagnostics.AddRange(result.Diagnostics.Select(item => new { artifact_id = artifact.Id, item.Code, item.Message, item.IsError }));
                }

                return new AnalyzeCommandResult(artifacts.Count, observations, diagnostics, hasErrors);
            },
            cancellationToken,
            result => result.HasErrors
                ? (ExecutionStatus.PartiallyFailed, "partial_parser_failure")
                : (ExecutionStatus.Completed, "complete")).ConfigureAwait(false);

        output.Write(
            new { analyzed = execution.Result.Analyzed, execution.Result.Observations, execution.Result.Diagnostics },
            settings.Json,
            execution.Run.Id.ToString(),
            execution.Run.CoverageStatus);
        return execution.Result.HasErrors ? 5 : 0;
    }
}

/// <summary>Normalizes extracted values into stable project entities.</summary>
public sealed class CorrelateCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CorrelationService correlation,
    RunCoordinator runs,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "correlate",
            ActivityMode.Passive,
            (_, token) => correlation.CorrelateAsync(project.Id, token),
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new { entities_added = execution.Result, entities_total = store.Entities.Count() },
            settings.Json,
            execution.Run.Id.ToString(),
            execution.Run.CoverageStatus);
        return 0;
    }
}

internal sealed record AnalyzeCommandResult(
    int Analyzed,
    int Observations,
    IReadOnlyList<object> Diagnostics,
    bool HasErrors);
