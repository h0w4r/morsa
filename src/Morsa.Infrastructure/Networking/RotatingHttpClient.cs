using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

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
    IProxyOutcomeRecorder outcomes)
{
    public async Task<HttpFetchResult> FetchAsync(
        Uri uri,
        string? poolName,
        NetworkRequestContext context,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var excluded = new HashSet<Guid>();
        var pool = poolName is null
            ? null
            : await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == poolName && item.Enabled, cancellationToken)
                .ConfigureAwait(false);
        var attempts = pool?.MaxAttempts ?? 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ProxyLease? lease = null;
            ProxyEndpoint? endpoint = null;
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
                using var handler = transports.CreateHttpHandler(endpoint);
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Morsa/1.0 (+https://github.com/h0w4r/morsa)");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                var content = await ReadBoundedAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
                timer.Stop();
                var outcome = Classify(response.StatusCode, content);
                var retryAfter = response.Headers.RetryAfter?.Delta;
                await outcomes.RecordAsync(
                    context,
                    lease,
                    new ProxyOutcome(outcome, timer.Elapsed, (int)response.StatusCode, content.Length, null, retryAfter, outcome.ToString()),
                    cancellationToken).ConfigureAwait(false);

                if (ShouldRotate(outcome) && endpoint is not null && excluded.Count < pool!.MaxRotations)
                {
                    excluded.Add(endpoint.Id);
                    if (lease is not null)
                    {
                        await proxyPool.ReleaseAsync(lease.Id, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
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
                    endpoint?.Id,
                    attempt);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
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
            }
        }

        throw new HttpRequestException("All bounded network attempts failed.", lastError);
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

