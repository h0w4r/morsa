using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Domain.Runs;

namespace Morsa.Application.Services;

/// <summary>Creates and closes durable runs without hiding partial completion.</summary>
public sealed class RunCoordinator(IMorsaStore store, IClock clock)
{
    /// <summary>
    /// Executes one durable use case and guarantees that cancellation or failure cannot leave
    /// a live process with a misleading <see cref="ExecutionStatus.Running"/> journal entry.
    /// </summary>
    public async Task<(Run Run, T Result)> ExecuteAsync<T>(
        Guid projectId,
        string command,
        ActivityMode mode,
        Func<Run, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Func<T, (ExecutionStatus Status, string Coverage)>? classify = null)
    {
        var run = await StartAsync(projectId, command, mode, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await operation(run, cancellationToken).ConfigureAwait(false);
            var completion = classify?.Invoke(result) ?? (ExecutionStatus.Completed, "complete");
            await CompleteAsync(run, completion.Status, completion.Coverage, cancellationToken).ConfigureAwait(false);
            return (run, result);
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

    /// <summary>Marks a durable task attempt before side effects begin.</summary>
    public async Task BeginTaskAsync(RunTask task, CancellationToken cancellationToken)
    {
        task.Status = ExecutionStatus.Running;
        task.AttemptCount++;
        task.LastErrorCode = null;
        task.LastErrorMessage = null;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the exact task outcome; partial work is never promoted to complete.</summary>
    public async Task CompleteTaskAsync(
        RunTask task,
        ExecutionStatus status,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        task.Status = status;
        task.LastErrorCode = errorCode;
        task.LastErrorMessage = errorMessage;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task CompleteBestEffortAsync(Run run, ExecutionStatus status, string coverage)
    {
        try
        {
            // The caller token may already be cancelled; journal closure gets an independent attempt.
            await CompleteAsync(run, status, coverage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original operation exception when durable storage is also unavailable.
        }
    }
}
