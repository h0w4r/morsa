using Morsa.Application.Abstractions;

namespace Morsa.Infrastructure.Time;

/// <summary>Production UTC clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

