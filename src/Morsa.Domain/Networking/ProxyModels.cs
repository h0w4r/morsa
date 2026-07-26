using Morsa.Domain.Common;

namespace Morsa.Domain.Networking;

/// <summary>Protocols supported by the proxy runtime.</summary>
public enum ProxyProtocol
{
    Http,
    HttpsConnect,
    Socks4,
    Socks5,
    Socks5Host,
}

/// <summary>Where hostname resolution occurs for a proxied connection.</summary>
public enum ProxyDnsMode
{
    Local,
    Remote,
}

/// <summary>Persistent health state of a proxy endpoint.</summary>
public enum ProxyStatus
{
    Unknown,
    Healthy,
    Degraded,
    Cooldown,
    Quarantined,
    Disabled,
    Unavailable,
}

/// <summary>Selection algorithms exposed by proxy profiles and CLI flags.</summary>
public enum ProxySelectionPolicy
{
    Sticky,
    RoundRobin,
    Random,
    Weighted,
    LeastLatency,
    Failover,
}

/// <summary>Classification recorded for every outbound attempt.</summary>
public enum NetworkOutcome
{
    Success,
    DnsFailure,
    ConnectFailure,
    TlsFailure,
    Timeout,
    ProxyAuthenticationRequired,
    Forbidden,
    RateLimited,
    ServerError,
    Challenge,
    ScopeRejected,
    Cancelled,
    UnknownFailure,
}

/// <summary>A named set of proxy endpoints and its rotation behavior.</summary>
public sealed class ProxyPool : Entity
{
    public required string Name { get; set; }

    public ProxySelectionPolicy SelectionPolicy { get; set; } = ProxySelectionPolicy.Sticky;

    public int MaxRotations { get; set; } = 5;

    public int MaxAttempts { get; set; } = 8;

    public int CooldownSeconds { get; set; } = 120;

    public int LeaseTtlSeconds { get; set; } = 900;

    public bool AllowDirectFallback { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>A proxy endpoint without inline credentials.</summary>
public sealed class ProxyEndpoint : Entity
{
    public Guid PoolId { get; set; }

    public required string Uri { get; set; }

    public ProxyProtocol Protocol { get; set; }

    public ProxyDnsMode DnsMode { get; set; }

    public string? SecretRef { get; set; }

    public int Weight { get; set; } = 1;

    public string TagsJson { get; set; } = "[]";

    public ProxyStatus Status { get; set; } = ProxyStatus.Unknown;

    public int ConsecutiveFailures { get; set; }

    public long SuccessCount { get; set; }

    public long FailureCount { get; set; }

    public double? EwmaLatencyMs { get; set; }

    public int MaxConcurrency { get; set; } = 4;

    public DateTimeOffset? CooldownUntil { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>Immutable health observation used for diagnostics and selection.</summary>
public sealed class ProxyHealthSample : Entity
{
    public Guid ProxyEndpointId { get; set; }

    public NetworkOutcome Outcome { get; set; }

    public double? LatencyMs { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Sticky assignment of a proxy endpoint to an execution session.</summary>
public sealed class ProxyLease : Entity
{
    public Guid? RunId { get; set; }

    public Guid? TaskId { get; set; }

    public Guid ProxyEndpointId { get; set; }

    public required string SessionKey { get; set; }

    public DateTimeOffset AcquiredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }
}

/// <summary>Auditable record of an individual direct or proxied network operation.</summary>
public sealed class NetworkAttempt : Entity
{
    public Guid? RunId { get; set; }

    public Guid? TaskId { get; set; }

    public Guid? ProxyEndpointId { get; set; }

    public required string Destination { get; set; }

    public NetworkOutcome Outcome { get; set; }

    public int? StatusCode { get; set; }

    public long BytesReceived { get; set; }

    public double DurationMs { get; set; }

    public string? RotationReason { get; set; }

    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
}

