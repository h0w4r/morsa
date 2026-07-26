using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

/// <summary>Shared network override flags accepted by acquisition and recon commands.</summary>
public class ProxyAwareSettings : WorkspaceSettings
{
    [CommandOption("--proxy <URI>")]
    public string? Proxy { get; init; }

    [CommandOption("--proxy-pool <POOL>")]
    public string? ProxyPool { get; init; }

    [CommandOption("--proxy-policy <POLICY>")]
    public string? ProxyPolicy { get; init; }

    [CommandOption("--max-proxy-rotations <COUNT>")]
    public int? MaxProxyRotations { get; init; }

    [CommandOption("--no-direct-fallback")]
    public bool NoDirectFallback { get; init; }
}

internal static class ProxyCliHelpers
{
    /// <summary>Resolves inline endpoints into the same persistent lease/health runtime as named pools.</summary>
    public static async Task<string?> ResolvePoolAsync(IMorsaStore store, ProxyAwareSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Proxy is not null && settings.ProxyPool is not null)
        {
            throw new InvalidOperationException("Use either --proxy or --proxy-pool, not both.");
        }

        ProxyPool? pool = null;
        if (settings.Proxy is not null)
        {
            var candidate = FileProxySource.Parse(settings.Proxy, null, 1, ["cli"]);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate.Uri.AbsoluteUri)))[..16].ToLowerInvariant();
            var name = $"__inline_{hash}";
            pool = await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == name, cancellationToken).ConfigureAwait(false);
            if (pool is null)
            {
                pool = new ProxyPool { Name = name, MaxRotations = 1, MaxAttempts = 1, AllowDirectFallback = false };
                store.Add(pool);
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                store.Add(new ProxyEndpoint
                {
                    PoolId = pool.Id,
                    Uri = candidate.Uri.AbsoluteUri,
                    Protocol = candidate.Protocol,
                    DnsMode = candidate.DnsMode,
                    TagsJson = "[\"cli\"]",
                });
            }
        }
        else if (settings.ProxyPool is not null)
        {
            pool = await store.ProxyPools.SingleOrDefaultAsync(item => item.Name == settings.ProxyPool, cancellationToken).ConfigureAwait(false) ??
                   throw new InvalidOperationException($"Proxy pool '{settings.ProxyPool}' does not exist.");
        }

        if (pool is null) return null;
        if (settings.ProxyPolicy is not null)
        {
            if (!Enum.TryParse<ProxySelectionPolicy>(settings.ProxyPolicy.Replace("-", string.Empty), true, out var policy))
            {
                throw new InvalidOperationException($"Unknown proxy policy '{settings.ProxyPolicy}'.");
            }
            pool.SelectionPolicy = policy;
        }
        if (settings.MaxProxyRotations is { } rotations)
        {
            pool.MaxRotations = Math.Clamp(rotations, 0, 10_000);
            pool.MaxAttempts = Math.Max(pool.MaxAttempts, pool.MaxRotations + 1);
        }
        if (settings.NoDirectFallback) pool.AllowDirectFallback = false;
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return pool.Name;
    }
}

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
    [CommandArgument(0, "<SOURCE>")]
    public required string Source { get; init; }

    [CommandOption("--pool <NAME>")]
    public string Pool { get; init; } = "default";
}

/// <summary>Imports sanitized endpoints without storing inline credentials.</summary>
public sealed class ProxyImportCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CompositeProxySource sources,
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

        var existing = await store.ProxyEndpoints.Where(item => item.PoolId == pool.Id).Select(item => item.Uri)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        var added = 0;
        await foreach (var candidate in sources.LoadAsync(settings.Source, cancellationToken).ConfigureAwait(false))
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
        output.Write(new { pool = pool.Name, imported = added, source = settings.Source }, settings.Json);
        return 0;
    }
}

/// <summary>Describes supported source adapters without reading or exposing proxy credentials.</summary>
public sealed class ProxySourceListCommand(CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var environmentVariables = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
        output.Write(new
        {
            adapters = new[]
            {
                new { id = "environment", syntax = "env", formats = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" } },
                new { id = "file", syntax = "PATH", formats = new[] { "text", "csv", "json", "ndjson" } },
                new { id = "stdin", syntax = "-", formats = new[] { "text", "jsonl" } },
                new { id = "https", syntax = "https://HOST/PATH", formats = new[] { "text", "csv", "json", "ndjson" } },
                new { id = "command", syntax = "command:EXECUTABLE [ARGUMENTS]", formats = new[] { "jsonl" } },
                new { id = "inline", syntax = "PROXY_URI", formats = new[] { "http", "https-connect", "socks4", "socks5", "socks5h" } },
            },
            environment = environmentVariables.ToDictionary(
                name => name.ToLowerInvariant(),
                name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))),
        }, settings.Json);
        return Task.FromResult(0);
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
        if (settings.Pool is not null && poolId is null)
        {
            // A typo must not broaden a targeted reset into resetting every configured endpoint.
            throw new InvalidOperationException($"Proxy pool '{settings.Pool}' does not exist.");
        }

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
    NetworkScopeValidator scopeValidator,
    INetworkTransportFactory transports,
    IProxyOutcomeRecorder recorder,
    CliOutput output) : AsyncCommand<ProxyTestSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ProxyTestSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
        {
            throw new InvalidDataException("Proxy test URL must be an absolute HTTP or HTTPS URL without user information.");
        }
        if (!await scopeValidator.IsAllowedAsync(project.Id, target, Morsa.Domain.Common.ActivityMode.Active, false, cancellationToken).ConfigureAwait(false))
        {
            return 3;
        }

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
