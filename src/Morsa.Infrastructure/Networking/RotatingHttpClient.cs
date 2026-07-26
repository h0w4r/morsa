using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Configuration;

namespace Morsa.Infrastructure.Networking;

/// <summary>Buffered HTTP result safe to use after the per-identity handler is disposed.</summary>
public sealed record HttpFetchResult(
    Uri Uri,
    HttpStatusCode StatusCode,
    byte[] Content,
    string? ContentType,
    IReadOnlyDictionary<string, string[]> Headers,
    Guid? ProxyEndpointId,
    int Attempts);

/// <summary>Executes aggressively rotating, bounded and fully journaled HTTP requests.</summary>
public sealed class RotatingHttpClient(
    IMorsaStore store,
    IProxyPool proxyPool,
    INetworkTransportFactory transports,
    IProxyOutcomeRecorder outcomes,
    EnvironmentProxyResolver environmentProxy,
    TargetRateLimiter rateLimiter,
    MorsaConfiguration configuration) : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<Guid>> _providerBlocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalGate = new(configuration.Network.Concurrency, configuration.Network.Concurrency);

    public async Task<HttpFetchResult> FetchAsync(
        Uri uri,
        string? poolName,
        NetworkRequestContext context,
        int maximumBytes,
        CancellationToken cancellationToken)
        => await FetchCoreAsync(uri, poolName, context, maximumBytes, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Fetches with DNS answers pinned between scope validation and a direct connection.</summary>
    public async Task<HttpFetchResult> FetchPinnedAsync(
        Uri uri,
        string? poolName,
        NetworkRequestContext context,
        int maximumBytes,
        IReadOnlyList<IPAddress> validatedAddresses,
        CancellationToken cancellationToken)
        => await FetchCoreAsync(uri, poolName, context, maximumBytes, validatedAddresses, cancellationToken).ConfigureAwait(false);

    private async Task<HttpFetchResult> FetchCoreAsync(
        Uri uri,
        string? poolName,
        NetworkRequestContext context,
        int maximumBytes,
        IReadOnlyList<IPAddress>? validatedAddresses,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var excluded = new HashSet<Guid>();
        var pool = poolName is null
            ? null
            : await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == poolName && item.Enabled, cancellationToken)
                .ConfigureAwait(false);
        if (poolName is not null && pool is null)
        {
            // A misspelled or disabled mandatory pool must never turn into a silent direct request.
            throw new InvalidOperationException($"Proxy pool '{poolName}' does not exist or is disabled.");
        }

        var attempts = pool?.MaxAttempts ?? 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await rateLimiter.WaitAsync(uri, cancellationToken).ConfigureAwait(false);
            ProxyLease? lease = null;
            ProxyEndpoint? endpoint = null;
            if (pool is null && poolName is null)
            {
                endpoint = environmentProxy.Resolve(uri);
            }
            if (pool is not null)
            {
                lease = await proxyPool.AcquireAsync(pool.Name, context, excluded, cancellationToken).ConfigureAwait(false);
                if (lease is not null)
                {
                    endpoint = await store.ProxyEndpoints.SingleAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (!pool.AllowDirectFallback)
                {
                    break;
                }
            }

            var timer = Stopwatch.StartNew();
            try
            {
                var client = GetClient(endpoint, context.SessionKey, uri, validatedAddresses);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Morsa/1.0 (+https://github.com/h0w4r/morsa)");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(configuration.Network.TimeoutSeconds));
                await _globalGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                        .ConfigureAwait(false);
                    var content = await ReadBoundedAsync(response.Content, maximumBytes, deadline.Token).ConfigureAwait(false);
                    timer.Stop();
                    var outcome = Classify(response.StatusCode, content);
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    await outcomes.RecordAsync(
                        context,
                        lease,
                        new ProxyOutcome(outcome, timer.Elapsed, (int)response.StatusCode, content.Length, null, retryAfter, outcome.ToString()),
                        cancellationToken).ConfigureAwait(false);

                    if (ShouldRotate(outcome) && endpoint is not null && pool is not null)
                    {
                        if (IsProviderCircuitOpen(context.ProviderId, endpoint.Id, pool.Id))
                        {
                            throw new HttpRequestException($"Provider '{context.ProviderId}' is blocked across multiple proxy identities.");
                        }

                        if (excluded.Count >= pool.MaxRotations)
                        {
                            throw new HttpRequestException($"Proxy rotation budget exhausted after {attempt} attempts.");
                        }
                        excluded.Add(endpoint.Id);
                        if (lease is not null)
                        {
                            await proxyPool.ReleaseAsync(lease.Id, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    if (ShouldRotate(outcome) && context.ProviderId is not null)
                    {
                        throw new HttpRequestException($"Provider '{context.ProviderId}' returned {outcome} and no further proxy rotation is available.");
                    }

                    var headers = response.Headers.Concat(response.Content.Headers)
                        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.SelectMany(item => item.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
                    return new HttpFetchResult(
                        uri,
                        response.StatusCode,
                        content,
                        response.Content.Headers.ContentType?.MediaType,
                        headers,
                        lease is null ? null : endpoint?.Id,
                        attempt);
                }
                finally
                {
                    _globalGate.Release();
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or TaskCanceledException &&
                !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                timer.Stop();
                lastError = exception;
                var outcome = exception switch
                {
                    TaskCanceledException when !cancellationToken.IsCancellationRequested => NetworkOutcome.Timeout,
                    HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => NetworkOutcome.TlsFailure,
                    HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => NetworkOutcome.DnsFailure,
                    _ => NetworkOutcome.ConnectFailure,
                };
                await outcomes.RecordAsync(
                    context,
                    lease,
                    new ProxyOutcome(outcome, timer.Elapsed, null, 0, exception.GetType().Name, null, outcome.ToString()),
                    cancellationToken).ConfigureAwait(false);
                if (endpoint is not null)
                {
                    excluded.Add(endpoint.Id);
                }
                if (lease is not null)
                {
                    // A failed identity must not consume pool concurrency until its lease TTL expires.
                    await proxyPool.ReleaseAsync(lease.Id, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        throw new HttpRequestException("All bounded network attempts failed.", lastError);
    }

    /// <summary>Disposes every identity-isolated cookie and connection pool at the end of the DI scope.</summary>
    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
        _clients.Clear();
        _globalGate.Dispose();
    }

    private HttpClient GetClient(
        ProxyEndpoint? endpoint,
        string sessionKey,
        Uri destination,
        IReadOnlyList<IPAddress>? validatedAddresses)
    {
        var identity = endpoint is null ? "direct" : endpoint.Id == Guid.Empty ? endpoint.Uri : endpoint.Id.ToString("N");
        var pinKey = endpoint is null && validatedAddresses is { Count: > 0 }
            ? string.Join(',', validatedAddresses.Select(address => address.ToString()).Order(StringComparer.Ordinal))
            : "unbound";
        var key = $"{sessionKey}\n{identity}\n{destination.IdnHost}:{destination.Port}\n{pinKey}";
        return _clients.GetOrAdd(key, _ =>
        {
            var handler = transports.CreateHttpHandler(endpoint);
            if (endpoint is null && validatedAddresses is { Count: > 0 } && handler is SocketsHttpHandler sockets)
            {
                var allowedHost = destination.IdnHost;
                var addresses = validatedAddresses.ToArray();
                sockets.ConnectCallback = async (connectContext, token) =>
                {
                    if (!connectContext.DnsEndPoint.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase))
                        throw new HttpRequestException("HTTP transport attempted to connect to a host outside its validated DNS pin.");

                    Exception? last = null;
                    foreach (var address in addresses)
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(address, connectContext.DnsEndPoint.Port), token).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                        {
                            socket.Dispose();
                            last = exception;
                            if (token.IsCancellationRequested) throw;
                        }
                    }

                    throw new HttpRequestException("None of the scope-validated DNS addresses accepted the connection.", last);
                };
            }

            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    private bool IsProviderCircuitOpen(string? providerId, Guid endpointId, Guid poolId)
    {
        if (providerId is null) return false;
        var blocked = _providerBlocks.GetOrAdd(providerId, _ => []);
        lock (blocked) blocked.Add(endpointId);
        var endpointCount = store.ProxyEndpoints.Count(endpoint => endpoint.PoolId == poolId);
        lock (blocked) return blocked.Count >= Math.Min(3, Math.Max(1, endpointCount));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("HTTP response exceeds the configured byte budget.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("HTTP response exceeded the configured byte budget while streaming.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static NetworkOutcome Classify(HttpStatusCode status, byte[] content)
    {
        if ((int)status == 407) return NetworkOutcome.ProxyAuthenticationRequired;
        if (status == HttpStatusCode.Forbidden) return NetworkOutcome.Forbidden;
        if ((int)status == 429) return NetworkOutcome.RateLimited;
        if ((int)status >= 500) return NetworkOutcome.ServerError;
        var preview = System.Text.Encoding.UTF8.GetString(content.AsSpan(0, Math.Min(content.Length, 64 * 1024)));
        if (preview.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            preview.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkOutcome.Challenge;
        }

        return (int)status is >= 200 and < 400 ? NetworkOutcome.Success : NetworkOutcome.UnknownFailure;
    }

    private static bool ShouldRotate(NetworkOutcome outcome) => outcome is
        NetworkOutcome.Timeout or NetworkOutcome.DnsFailure or NetworkOutcome.ConnectFailure or
        NetworkOutcome.TlsFailure or NetworkOutcome.ProxyAuthenticationRequired or NetworkOutcome.Forbidden or
        NetworkOutcome.RateLimited or NetworkOutcome.ServerError or NetworkOutcome.Challenge;
}
