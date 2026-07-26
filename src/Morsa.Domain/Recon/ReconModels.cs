using Morsa.Domain.Common;

namespace Morsa.Domain.Recon;

/// <summary>DNS record collected with source and timestamp.</summary>
public sealed class DnsObservation : Entity
{
    public Guid RunId { get; set; }
    public required string Name { get; set; }
    public required string RecordType { get; set; }
    public required string Value { get; set; }
    public uint? Ttl { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Network service observed during authorized validation.</summary>
public sealed class ServiceObservation : Entity
{
    public Guid RunId { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }
    public required string Protocol { get; set; }
    public string? Banner { get; set; }
    public string? TlsSubject { get; set; }
    public string? TlsIssuer { get; set; }
    public string? Technology { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Static content-risk observation; no external upload is implied.</summary>
public sealed class MalwareObservation : Entity
{
    public Guid RunId { get; set; }
    public Guid ArtifactId { get; set; }
    public required string Kind { get; set; }
    public required string Value { get; set; }
    public string Severity { get; set; } = "informational";
    public string Source { get; set; } = "builtin";
}

/// <summary>Auditable execution of an installed plugin.</summary>
public sealed class PluginExecution : Entity
{
    public Guid? RunId { get; set; }
    public required string PluginId { get; set; }
    public required string PluginVersion { get; set; }
    public string Status { get; set; } = "running";
    public int? ExitCode { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

