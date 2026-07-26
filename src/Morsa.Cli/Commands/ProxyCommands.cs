using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class ProxyPoolAddSettings : WorkspaceSettings
{
    [CommandArgument(0, "<NAME>")]
    public required string Name { get; init; }

    [CommandOption("--policy <POLICY>")]
    public string Policy { get; init; } = "sticky";

    [CommandOption("--max-rotations <COUNT>")]
    public int MaxRotations { get; init; } = 5;

    [CommandOption("--max-attempts <COUNT>")]
    public int MaxAttempts { get; init; } = 8;

    [CommandOption("--allow-direct-fallback")]
    public bool AllowDirectFallback { get; init; }
}

/// <summary>Creates or updates one proxy pool policy.</summary>
public sealed class ProxyPoolAddCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ProxyPoolAddSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyPoolAddSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        if (!Enum.TryParse<ProxySelectionPolicy>(settings.Policy.Replace("-", string.Empty), true, out var policy))
        {
            throw new InvalidOperationException("Unknown proxy policy.");
        }

        var pool = await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == settings.Name, cancellationToken)
            .ConfigureAwait(false);
        if (pool is null)
        {
            pool = new ProxyPool { Name = settings.Name };
            store.Add(pool);
        }

        pool.SelectionPolicy = policy;
        pool.MaxRotations = Math.Max(1, settings.MaxRotations);
        pool.MaxAttempts = Math.Max(pool.MaxRotations, settings.MaxAttempts);
        pool.AllowDirectFallback = settings.AllowDirectFallback;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        output.Write(pool, settings.Json);
        return 0;
    }
}

/// <summary>Lists pools and endpoint counts.</summary>
public sealed class ProxyPoolListCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var pools = await store.ProxyPools.Select(pool => new
        {
            pool.Id,
            pool.Name,
            pool.SelectionPolicy,
            pool.MaxRotations,
            pool.MaxAttempts,
            pool.AllowDirectFallback,
            endpoints = store.ProxyEndpoints.Count(endpoint => endpoint.PoolId == pool.Id),
        }).ToListAsync(cancellationToken).ConfigureAwait(false);
        output.Write(pools, settings.Json);
        return 0;
    }
}

public sealed class ProxyImportSettings : WorkspaceSettings
{
    [CommandArgument(0, "<FILE>")]
    public required string File { get; init; }

    [CommandOption("--pool <NAME>")]
    public string Pool { get; init; } = "default";
}

/// <summary>Imports sanitized endpoints without storing inline credentials.</summary>
public sealed class ProxyImportCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ProxyImportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyImportSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var pool = await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == settings.Pool, cancellationToken)
            .ConfigureAwait(false);
        if (pool is null)
        {
            pool = new ProxyPool { Name = settings.Pool };
            store.Add(pool);
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var source = new FileProxySource(Path.GetFullPath(settings.File));
        var existing = await store.ProxyEndpoints.Where(item => item.PoolId == pool.Id).Select(item => item.Uri)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var added = 0;
        await foreach (var candidate in source.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!existing.Add(candidate.Uri.ToString()))
            {
                continue;
            }

            store.Add(new ProxyEndpoint
            {
                PoolId = pool.Id,
                Uri = candidate.Uri.ToString(),
                Protocol = candidate.Protocol,
                DnsMode = candidate.DnsMode,
                SecretRef = candidate.SecretRef,
                Weight = candidate.Weight,
                TagsJson = JsonSerializer.Serialize(candidate.Tags),
            });
            added++;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        output.Write(new { pool = pool.Name, imported = added, source = source.Id }, settings.Json);
        return 0;
    }
}

public sealed class ProxyPoolNameSettings : WorkspaceSettings
{
    [CommandOption("--pool <NAME>")]
    public string? Pool { get; init; }
}

/// <summary>Displays only redacted endpoint addresses and health.</summary>
public sealed class ProxyStatusCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ProxyPoolNameSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyPoolNameSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var query = from endpoint in store.ProxyEndpoints
                    join pool in store.ProxyPools on endpoint.PoolId equals pool.Id
                    where settings.Pool == null || pool.Name == settings.Pool
                    orderby pool.Name, endpoint.Uri
                    select new
                    {
                        pool = pool.Name,
                        endpoint.Id,
                        endpoint.Uri,
                        endpoint.Protocol,
                        endpoint.DnsMode,
                        endpoint.Status,
                        endpoint.SuccessCount,
                        endpoint.FailureCount,
                        endpoint.EwmaLatencyMs,
                        endpoint.CooldownUntil,
                        has_secret = endpoint.SecretRef != null,
                    };
        output.Write(await query.ToListAsync(cancellationToken).ConfigureAwait(false), settings.Json);
        return 0;
    }
}

/// <summary>Clears transient health without deleting endpoints or credentials references.</summary>
public sealed class ProxyResetCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<ProxyPoolNameSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyPoolNameSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var poolId = settings.Pool is null
            ? (Guid?)null
            : await store.ProxyPools.Where(item => item.Name == settings.Pool).Select(item => (Guid?)item.Id)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = await store.ProxyEndpoints
            .Where(item => poolId == null || item.PoolId == poolId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var endpoint in endpoints)
        {
            endpoint.Status = ProxyStatus.Unknown;
            endpoint.ConsecutiveFailures = 0;
            endpoint.CooldownUntil = null;
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        output.Write(new { reset = endpoints.Count }, settings.Json);
        return 0;
    }
}

public sealed class ProxyTestSettings : WorkspaceSettings
{
    [CommandArgument(0, "<POOL>")]
    public required string Pool { get; init; }

    [CommandOption("--url <URL>")]
    public string Url { get; init; } = "https://example.com/";
}

/// <summary>Runs bounded endpoint healthchecks and records actual outcomes.</summary>
public sealed class ProxyTestCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    INetworkTransportFactory transports,
    IProxyOutcomeRecorder recorder,
    CliOutput output) : AsyncCommand<ProxyTestSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyTestSettings settings, CancellationToken cancellationToken)
    {
        await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var target = new Uri(settings.Url, UriKind.Absolute);
        var pool = await store.ProxyPools.SingleAsync(item => item.Name == settings.Pool, cancellationToken).ConfigureAwait(false);
        var endpoints = await store.ProxyEndpoints.Where(item => item.PoolId == pool.Id).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<object>();
        foreach (var endpoint in endpoints)
        {
            var timer = Stopwatch.StartNew();
            NetworkOutcome networkOutcome;
            int? statusCode = null;
            string? error = null;
            try
            {
                using var client = new HttpClient(transports.CreateHttpHandler(endpoint), disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(20),
                };
                using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                statusCode = (int)response.StatusCode;
                networkOutcome = statusCode switch
                {
                    403 => NetworkOutcome.Forbidden,
                    407 => NetworkOutcome.ProxyAuthenticationRequired,
                    429 => NetworkOutcome.RateLimited,
                    >= 500 => NetworkOutcome.ServerError,
                    _ when response.IsSuccessStatusCode => NetworkOutcome.Success,
                    _ => NetworkOutcome.UnknownFailure,
                };
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                networkOutcome = NetworkOutcome.Timeout;
                error = exception.GetType().Name;
            }
            catch (HttpRequestException exception)
            {
                networkOutcome = NetworkOutcome.ConnectFailure;
                error = exception.HttpRequestError.ToString();
            }

            timer.Stop();
            var lease = new ProxyLease
            {
                ProxyEndpointId = endpoint.Id,
                SessionKey = $"health:{endpoint.Id}",
                ExpiresAt = DateTimeOffset.UtcNow,
            };
            var requestContext = new NetworkRequestContext(null, null, lease.SessionKey, target, "proxy-health", null);
            await recorder.RecordAsync(
                requestContext,
                lease,
                new ProxyOutcome(networkOutcome, timer.Elapsed, statusCode, 0, error, null, networkOutcome.ToString()),
                cancellationToken).ConfigureAwait(false);
            results.Add(new { endpoint = endpoint.Uri, outcome = networkOutcome, status_code = statusCode, duration_ms = timer.Elapsed.TotalMilliseconds });
        }

        output.Write(results, settings.Json);
        return results.Count == 0 ? 4 : 0;
    }
}


