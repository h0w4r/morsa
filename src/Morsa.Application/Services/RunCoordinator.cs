using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Runs;

namespace Morsa.Application.Services;

/// <summary>Creates and closes durable runs without hiding partial completion.</summary>
public sealed class RunCoordinator(IMorsaStore store, IClock clock)
{
    public async Task<Run> StartAsync(
        Guid projectId,
        string command,
        ActivityMode mode,
        CancellationToken cancellationToken)
    {
        var run = new Run
        {
            ProjectId = projectId,
            Command = command,
            Mode = mode,
            Status = ExecutionStatus.Running,
            StartedAt = clock.UtcNow,
        };

        store.Add(run);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<RunTask> GetOrCreateTaskAsync(
        Run run,
        string kind,
        string idempotencyKey,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var existing = store.Tasks.FirstOrDefault(task =>
            task.RunId == run.Id && task.IdempotencyKey == idempotencyKey);

        if (existing is not null)
        {
            return existing;
        }

        var task = new RunTask
        {
            RunId = run.Id,
            Kind = kind,
            IdempotencyKey = idempotencyKey,
            PayloadJson = payloadJson,
        };

        store.Add(task);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task CompleteAsync(
        Run run,
        ExecutionStatus status,
        string coverage,
        CancellationToken cancellationToken)
    {
        run.Status = status;
        run.CoverageStatus = coverage;
        run.FinishedAt = clock.UtcNow;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

