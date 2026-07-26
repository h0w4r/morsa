namespace Morsa.Domain.Common;

/// <summary>
/// Base class for durable domain entities. Identifiers are generated client-side so
/// work can be journaled before an external operation starts.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

