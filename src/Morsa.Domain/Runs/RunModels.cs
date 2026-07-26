using Morsa.Domain.Common;

namespace Morsa.Domain.Runs;

/// <summary>Durable execution record used to resume interrupted pipelines.</summary>
public sealed class Run : Entity
{
    public Guid ProjectId { get; set; }

    public required string Command { get; set; }

    public ActivityMode Mode { get; set; }

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    public string CoverageStatus { get; set; } = "unknown";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Idempotent unit of work belonging to a <see cref="Run"/>.</summary>
public sealed class RunTask : Entity
{
    public Guid RunId { get; set; }

    public required string Kind { get; set; }

    public required string IdempotencyKey { get; set; }

    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;

    public int AttemptCount { get; set; }

    public DateTimeOffset? NextRetryAt { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

