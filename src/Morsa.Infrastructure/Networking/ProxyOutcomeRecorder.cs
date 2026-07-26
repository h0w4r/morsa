using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>Updates health atomically and records every attempt without secrets.</summary>
public sealed class ProxyOutcomeRecorder(
    IMorsaStore store,
    IClock clock) : IProxyOutcomeRecorder
{
    public async Task RecordAsync(
        NetworkRequestContext context,
        ProxyLease? lease,
        ProxyOutcome outcome,
        CancellationToken cancellationToken)
    {
        ProxyEndpoint? endpoint = null;
        if (lease is not null)
        {
            endpoint = await store.ProxyEndpoints
                .SingleOrDefaultAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (endpoint is not null)
        {
            ApplyOutcome(endpoint, outcome);
            endpoint.LastCheckedAt = clock.UtcNow;
            store.Add(new ProxyHealthSample
            {
                ProxyEndpointId = endpoint.Id,
                Outcome = outcome.Outcome,
                LatencyMs = outcome.Duration.TotalMilliseconds,
                ErrorCode = outcome.ErrorCode,
                ObservedAt = clock.UtcNow,
            });
        }

        store.Add(new NetworkAttempt
        {
            RunId = context.RunId,
            TaskId = context.TaskId,
            ProxyEndpointId = endpoint?.Id,
            Destination = RedactDestination(context.Destination),
            Outcome = outcome.Outcome,
            StatusCode = outcome.StatusCode,
            BytesReceived = outcome.BytesReceived,
            DurationMs = outcome.Duration.TotalMilliseconds,
            RotationReason = outcome.RotationReason,
            AttemptedAt = clock.UtcNow,
        });

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyOutcome(ProxyEndpoint endpoint, ProxyOutcome outcome)
    {
        if (outcome.Outcome == NetworkOutcome.Success)
        {
            endpoint.Status = ProxyStatus.Healthy;
            endpoint.ConsecutiveFailures = 0;
            endpoint.SuccessCount++;
            endpoint.CooldownUntil = null;
            endpoint.EwmaLatencyMs = endpoint.EwmaLatencyMs is null
                ? outcome.Duration.TotalMilliseconds
                : (endpoint.EwmaLatencyMs * 0.8) + (outcome.Duration.TotalMilliseconds * 0.2);
            return;
        }

        endpoint.FailureCount++;
        endpoint.ConsecutiveFailures++;
        var rotates = outcome.Outcome is NetworkOutcome.Timeout or
            NetworkOutcome.DnsFailure or
            NetworkOutcome.ConnectFailure or
            NetworkOutcome.TlsFailure or
            NetworkOutcome.ProxyAuthenticationRequired or
            NetworkOutcome.Forbidden or
            NetworkOutcome.RateLimited or
            NetworkOutcome.ServerError or
            NetworkOutcome.Challenge;

        if (rotates)
        {
            endpoint.Status = ProxyStatus.Cooldown;
            endpoint.CooldownUntil = clock.UtcNow.Add(outcome.RetryAfter ?? TimeSpan.FromMinutes(2));
        }
        else
        {
            endpoint.Status = ProxyStatus.Degraded;
        }
    }

    private static string RedactDestination(Uri destination) =>
        destination.IsDefaultPort
            ? $"{destination.Scheme}://{destination.IdnHost}{destination.AbsolutePath}"
            : $"{destination.Scheme}://{destination.IdnHost}:{destination.Port}{destination.AbsolutePath}";
}

