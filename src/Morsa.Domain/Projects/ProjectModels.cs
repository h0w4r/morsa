using Morsa.Domain.Common;

namespace Morsa.Domain.Projects;

/// <summary>Represents a self-contained Morsa investigation workspace.</summary>
public sealed class MorsaProject : Entity
{
    public required string Name { get; set; }

    public required string RootPath { get; set; }

    public ActivityMode DefaultMode { get; set; } = ActivityMode.Passive;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Stores an authorized target and the highest permitted activity level.</summary>
public sealed class ScopeEntry : Entity
{
    public Guid ProjectId { get; set; }

    public required string Value { get; set; }

    public required string Kind { get; set; }

    public ActivityMode MaximumMode { get; set; } = ActivityMode.Passive;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

