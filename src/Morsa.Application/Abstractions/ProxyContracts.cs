using System.Net;
using Morsa.Domain.Networking;

namespace Morsa.Application.Abstractions;

/// <summary>Normalized proxy candidate loaded from a user or plugin source.</summary>
public sealed record ProxyCandidate(
    Uri Uri,
    ProxyProtocol Protocol,
    ProxyDnsMode DnsMode,
    string? SecretRef,
    int Weight,
    IReadOnlyCollection<string> Tags);

/// <summary>Context used to select and audit a proxy lease.</summary>
public sealed record NetworkRequestContext(
    Guid? RunId,
    Guid? TaskId,
    string SessionKey,
    Uri Destination,
    string Module,
    string? ProviderId,
    ProxyProtocol? RequiredProtocol = null);

/// <summary>Outcome reported after an outbound operation.</summary>
public sealed record ProxyOutcome(
    NetworkOutcome Outcome,
    TimeSpan Duration,
    int? StatusCode,
    long BytesReceived,
    string? ErrorCode,
    TimeSpan? RetryAfter,
    string? RotationReason);

/// <summary>Loads proxy candidates from one configured source.</summary>
public interface IProxySource
{
    string Id { get; }

    IAsyncEnumerable<ProxyCandidate> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Acquires sticky or rotating endpoints from a named pool.</summary>
public interface IProxyPool
{
    Task<ProxyLease?> AcquireAsync(
        string poolName,
        NetworkRequestContext context,
        IReadOnlySet<Guid>? excludedEndpoints,
        CancellationToken cancellationToken);

    Task ReleaseAsync(Guid leaseId, CancellationToken cancellationToken);
}

/// <summary>Pure selection algorithm used by the persistent proxy pool.</summary>
public interface IProxySelectionPolicy
{
    ProxyEndpoint? Select(
        ProxySelectionPolicy policy,
        IReadOnlyList<ProxyEndpoint> eligible,
        string sessionKey,
        long sequence);
}

/// <summary>Builds HTTP and raw TCP transports for a proxy lease.</summary>
public interface INetworkTransportFactory
{
    HttpMessageHandler CreateHttpHandler(ProxyEndpoint? endpoint);

    Task<Stream> ConnectTcpAsync(
        ProxyEndpoint? endpoint,
        string host,
        int port,
        CancellationToken cancellationToken);
}

/// <summary>Persists health and audit data after every network attempt.</summary>
public interface IProxyOutcomeRecorder
{
    Task RecordAsync(
        NetworkRequestContext context,
        ProxyLease? lease,
        ProxyOutcome outcome,
        CancellationToken cancellationToken);
}

/// <summary>Resolves a secret reference without exposing it in configuration models.</summary>
public interface ISecretResolver
{
    NetworkCredential? ResolveNetworkCredential(string? secretRef);
}

