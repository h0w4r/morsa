using System.Net;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Cli.Runtime;
using Morsa.Domain.Common;
using Morsa.Domain.Networking;
using Morsa.Infrastructure.Malware;
using Morsa.Infrastructure.Networking;
using Morsa.Infrastructure.Recon;
using Morsa.Infrastructure.Reporting;
using Morsa.Infrastructure.Web;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class ReconDnsSettings : ProxyAwareSettings
{
    [CommandArgument(0, "<NAME>")]
    public required string Name { get; init; }

    [CommandOption("--types <TYPES>")]
    public string Types { get; init; } = "A,AAAA,MX,NS,SOA,TXT,CNAME,SRV,CAA";

    [CommandOption("--dns-server <HOST>")]
    public string DnsServer { get; init; } = "1.1.1.1";
}

/// <summary>Queries the requested DNS record types and journals the run.</summary>
public sealed class ReconDnsCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    ScopePolicy scopePolicy,
    IProxyPool proxyRuntime,
    DnsReconService dns,
    CliOutput output) : AsyncCommand<ReconDnsSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconDnsSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{settings.Name.TrimEnd('.')}/"), ActivityMode.Passive, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "recon dns", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        var types = settings.Types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.Parse<QueryType>(value, true)).ToArray();
        IReadOnlyList<Morsa.Domain.Recon.DnsObservation> observations;
        if (proxyPool is null)
        {
            observations = await dns.QueryAsync(run.Id, settings.Name, types, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var networkContext = new NetworkRequestContext(run.Id, null, $"dns:{run.Id:N}", new Uri($"https://{settings.Name.TrimEnd('.')}/"), "dns", null, ProxyProtocol.Socks5Host);
            var lease = await proxyRuntime.AcquireAsync(proxyPool, networkContext, null, cancellationToken).ConfigureAwait(false) ??
                        throw new InvalidOperationException("No compatible proxy endpoint is available for DNS.");
            try
            {
                var endpoint = await store.ProxyEndpoints.SingleAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken).ConfigureAwait(false);
                observations = await dns.QueryViaProxyAsync(run.Id, settings.Name, types, endpoint, settings.DnsServer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await proxyRuntime.ReleaseAsync(lease.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(observations, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class ReconReverseSettings : ProxyAwareSettings
{
    [CommandArgument(0, "<ADDRESSES>")]
    public required string Addresses { get; init; }

    [CommandOption("--dns-server <HOST>")]
    public string DnsServer { get; init; } = "1.1.1.1";
}

/// <summary>Resolves comma-separated IP addresses to PTR records.</summary>
public sealed class ReconReverseCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    ScopePolicy scopePolicy,
    IProxyPool proxyRuntime,
    DnsReconService dns,
    CliOutput output) : AsyncCommand<ReconReverseSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconReverseSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var parsedAddresses = settings.Addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(IPAddress.Parse).ToArray();
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (parsedAddresses.Any(address => !scopePolicy.IsUriAllowed(new Uri($"https://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}/"), ActivityMode.Passive, scope, false))) return 3;
        var run = await runs.StartAsync(project.Id, "recon reverse", ActivityMode.Passive, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Morsa.Domain.Recon.DnsObservation> results;
        if (proxyPool is null)
        {
            results = await dns.ReverseAsync(run.Id, parsedAddresses, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var networkContext = new NetworkRequestContext(run.Id, null, $"reverse:{run.Id:N}", new Uri($"https://{settings.DnsServer}/"), "reverse-dns", null, ProxyProtocol.Socks5Host);
            var lease = await proxyRuntime.AcquireAsync(proxyPool, networkContext, null, cancellationToken).ConfigureAwait(false) ??
                        throw new InvalidOperationException("No compatible proxy endpoint is available for reverse DNS.");
            try
            {
                var endpoint = await store.ProxyEndpoints.SingleAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken).ConfigureAwait(false);
                results = await dns.ReverseViaProxyAsync(run.Id, parsedAddresses, endpoint, settings.DnsServer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await proxyRuntime.ReleaseAsync(lease.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(results, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class ReconSubdomainsSettings : WorkspaceSettings
{
    [CommandArgument(0, "<DOMAIN>")]
    public required string Domain { get; init; }

    [CommandOption("--wordlist <FILE>")]
    public string? Wordlist { get; init; }

    [CommandOption("--budget <COUNT>")]
    public int Budget { get; init; } = 1_000;
}

/// <summary>Performs bounded active DNS label enumeration with wildcard suppression.</summary>
public sealed class ReconSubdomainsCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, DnsReconService dns, CliOutput output) : AsyncCommand<ReconSubdomainsSettings>
{
    private static readonly string[] DefaultLabels =
        ["www", "mail", "smtp", "imap", "pop", "vpn", "remote", "portal", "intranet", "dev", "test", "qa", "stage", "staging", "api", "app", "admin", "auth", "sso", "cdn", "static", "files", "ftp", "ns1", "ns2", "mx", "git", "jenkins", "grafana", "status"];

    protected override async Task<int> ExecuteAsync(CommandContext context, ReconSubdomainsSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var domain = settings.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{domain}/"), ActivityMode.Active, scope, false)) return 3;
        var labels = settings.Wordlist is null
            ? DefaultLabels
            : await File.ReadAllLinesAsync(Path.GetFullPath(settings.Wordlist), cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "recon subdomains", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var observations = await dns.DiscoverSubdomainsAsync(run.Id, domain, labels, Math.Clamp(settings.Budget, 1, 100_000), cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(observations, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class ReconRangeSettings : WorkspaceSettings
{
    [CommandArgument(0, "<CIDR>")]
    public required string Cidr { get; init; }

    [CommandOption("--budget <COUNT>")]
    public int Budget { get; init; } = 4_096;
}

/// <summary>Performs bounded PTR enumeration for an authorized address prefix.</summary>
public sealed class ReconRangeCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, DnsReconService dns, CliOutput output) : AsyncCommand<ReconRangeSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconRangeSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var addresses = EnumeratePrefix(settings.Cidr, Math.Clamp(settings.Budget, 1, 65_536)).ToArray();
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (addresses.Any(address => !scopePolicy.IsUriAllowed(ToScopeUri(address), ActivityMode.Active, scope, false))) return 3;
        var run = await runs.StartAsync(project.Id, "recon range", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var observations = await dns.ReverseAsync(run.Id, addresses, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(observations, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }

    private static IEnumerable<IPAddress> EnumeratePrefix(string cidr, int budget)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new InvalidDataException("CIDR must be an IPv4 or IPv6 prefix such as 192.0.2.0/24.");
        var bytes = address.GetAddressBytes();
        if (prefix < 0 || prefix > bytes.Length * 8) throw new InvalidDataException("CIDR prefix length is invalid.");
        var network = bytes.ToArray();
        for (var bit = prefix; bit < network.Length * 8; bit++) network[bit / 8] &= (byte)~(1 << (7 - (bit % 8)));
        var current = network.ToArray();
        for (var count = 0; count < budget && MatchesPrefix(network, current, prefix); count++)
        {
            yield return new IPAddress(current);
            for (var index = current.Length - 1; index >= 0 && ++current[index] == 0; index--) { }
        }
    }

    private static bool MatchesPrefix(byte[] network, byte[] value, int prefix)
    {
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = 1 << (7 - (bit % 8));
            if ((network[bit / 8] & mask) != (value[bit / 8] & mask)) return false;
        }
        return true;
    }

    private static Uri ToScopeUri(IPAddress address) =>
        new($"https://{(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}/");
}

public sealed class ReconAxfrSettings : WorkspaceSettings
{
    [CommandArgument(0, "<ZONE>")]
    public required string Zone { get; init; }

    [CommandOption("--server <NAME>")]
    public string? Server { get; init; }
}

/// <summary>Attempts an explicitly authorized zone transfer over TCP.</summary>
public sealed class ReconAxfrCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, NetworkScopeValidator scopeValidator, DnsReconService dns, CliOutput output) : AsyncCommand<ReconAxfrSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReconAxfrSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var zone = settings.Zone.Trim().TrimEnd('.').ToLowerInvariant();
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{zone}/"), ActivityMode.Aggressive, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "recon axfr", ActivityMode.Aggressive, cancellationToken).ConfigureAwait(false);
        try
        {
            var server = settings.Server;
            if (server is null)
            {
                server = (await dns.QueryAsync(run.Id, zone, [QueryType.NS], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Value;
            }
            if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("No authoritative name server was discovered; use --server.");
            var serverUri = new UriBuilder("https", server.Trim().TrimEnd('.'), 53).Uri;
            var validatedAddresses = await scopeValidator.ResolveAllowedAddressesAsync(
                project.Id, serverUri, ActivityMode.Aggressive, false, cancellationToken).ConfigureAwait(false);
            if (validatedAddresses is null)
            {
                await runs.CompleteAsync(run, ExecutionStatus.Failed, "scope_rejected", cancellationToken).ConfigureAwait(false);
                return 3;
            }

            // Pass the already validated address to close the DNS validation/connect race.
            var observations = await dns.ZoneTransferAsync(run.Id, zone, validatedAddresses[0].ToString(), cancellationToken).ConfigureAwait(false);
            await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
            output.Write(observations, settings.Json, run.Id.ToString(), "complete");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }
}

internal static class ReconRunJournal
{
    /// <summary>Prevents an exhausted mandatory pool from degrading into a direct TCP connection.</summary>
    public static async Task EnsureTcpFallbackAllowedAsync(
        IMorsaStore store,
        string? poolName,
        ProxyLease? lease,
        CancellationToken cancellationToken)
    {
        if (poolName is null || lease is not null) return;
        var allowDirect = await store.ProxyPools
            .Where(pool => pool.Name == poolName && pool.Enabled)
            .Select(pool => pool.AllowDirectFallback)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!allowDirect)
        {
            throw new InvalidOperationException($"Proxy pool '{poolName}' has no eligible endpoint and direct fallback is disabled.");
        }
    }

    public static async Task CompleteRunBestEffortAsync(
        RunCoordinator coordinator,
        Morsa.Domain.Runs.Run run,
        ExecutionStatus status,
        string coverage)
    {
        try
        {
            await coordinator.CompleteAsync(run, status, coverage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original network or cancellation exception remains authoritative.
        }
    }
}

public class FingerprintHttpSettings : ProxyAwareSettings
{
    [CommandArgument(0, "<URL>")]
    public required string Url { get; init; }

}

/// <summary>Fingerprints an HTTP endpoint after explicit scope validation.</summary>
public sealed class FingerprintHttpCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    RunCoordinator runs,
    ScopePolicy scopePolicy,
    FingerprintService fingerprint,
    CliOutput output) : AsyncCommand<FingerprintHttpSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FingerprintHttpSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var uri = new Uri(settings.Url);
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(uri, ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint http", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await fingerprint.FingerprintHttpAsync(project.Id, run.Id, uri, proxyPool, cancellationToken).ConfigureAwait(false);
            await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
            output.Write(result, settings.Json, run.Id.ToString(), "complete");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
    }
}

public class HostPortSettings : ProxyAwareSettings
{
    [CommandArgument(0, "<HOST>")]
    public required string Host { get; init; }

    [CommandOption("--port <PORT>")]
    public int Port { get; init; }
}

/// <summary>Collects TLS certificate evidence.</summary>
public sealed class FingerprintTlsCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, IProxyPool proxyRuntime, FingerprintService fingerprint, CliOutput output) : AsyncCommand<HostPortSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, HostPortSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var port = settings.Port == 0 ? 443 : settings.Port;
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{settings.Host}:{port}/"), ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint tls", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var contextValue = new NetworkRequestContext(run.Id, null, $"tls:{run.Id:N}", new Uri($"https://{settings.Host}:{port}/"), "fingerprint-tls", null);
        ProxyLease? lease = null;
        try
        {
            lease = proxyPool is null ? null : await proxyRuntime.AcquireAsync(proxyPool, contextValue, null, cancellationToken).ConfigureAwait(false);
            await ReconRunJournal.EnsureTcpFallbackAllowedAsync(store, proxyPool, lease, cancellationToken).ConfigureAwait(false);
            var endpoint = lease is null ? null : await store.ProxyEndpoints.SingleAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken).ConfigureAwait(false);
            var result = await fingerprint.InspectTlsAsync(project.Id, run.Id, settings.Host, port, endpoint, cancellationToken).ConfigureAwait(false);
            await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
            output.Write(result, settings.Json, run.Id.ToString(), "complete");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lease is not null) await proxyRuntime.ReleaseAsync(lease.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public sealed class FingerprintBannerSettings : HostPortSettings
{
    [CommandOption("--protocol <PROTOCOL>")]
    public string Protocol { get; init; } = "tcp";
}

/// <summary>Collects one bounded raw service banner.</summary>
public sealed class FingerprintBannerCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    ScopePolicy scopePolicy, IProxyPool proxyRuntime, FingerprintService fingerprint, CliOutput output) : AsyncCommand<FingerprintBannerSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, FingerprintBannerSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        if (settings.Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        var scope = await store.ScopeEntries.Where(item => item.ProjectId == project.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!scopePolicy.IsUriAllowed(new Uri($"https://{settings.Host}:{settings.Port}/"), ActivityMode.Active, scope, false)) return 3;
        var run = await runs.StartAsync(project.Id, "fingerprint banner", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var contextValue = new NetworkRequestContext(run.Id, null, $"banner:{run.Id:N}", new Uri($"https://{settings.Host}:{settings.Port}/"), "fingerprint-banner", null);
        ProxyLease? lease = null;
        try
        {
            lease = proxyPool is null ? null : await proxyRuntime.AcquireAsync(proxyPool, contextValue, null, cancellationToken).ConfigureAwait(false);
            await ReconRunJournal.EnsureTcpFallbackAllowedAsync(store, proxyPool, lease, cancellationToken).ConfigureAwait(false);
            var endpoint = lease is null ? null : await store.ProxyEndpoints.SingleAsync(item => item.Id == lease.ProxyEndpointId, cancellationToken).ConfigureAwait(false);
            var result = await fingerprint.GrabBannerAsync(project.Id, run.Id, settings.Host, settings.Port, settings.Protocol, endpoint, cancellationToken).ConfigureAwait(false);
            await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
            output.Write(result, settings.Json, run.Id.ToString(), "complete");
            return 0;
        }
        catch (OperationCanceledException)
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Cancelled, "cancelled").ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ReconRunJournal.CompleteRunBestEffortAsync(runs, run, ExecutionStatus.Failed, "failed").ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lease is not null) await proxyRuntime.ReleaseAsync(lease.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public sealed class WebCrawlSettings : FingerprintHttpSettings
{
    [CommandOption("--depth <DEPTH>")]
    public int Depth { get; init; } = 3;

    [CommandOption("--max-pages <COUNT>")]
    public int MaxPages { get; init; } = 500;
}

public sealed class WebCrawlCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    WebMappingService web, CliOutput output) : AsyncCommand<WebCrawlSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WebCrawlSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "web crawl", ActivityMode.Active, cancellationToken).ConfigureAwait(false);
        var results = await web.CrawlAsync(project.Id, run.Id, new Uri(settings.Url), Math.Clamp(settings.Depth, 0, 10), Math.Clamp(settings.MaxPages, 1, 100_000), proxyPool, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(new { discovered = results.Count }, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public sealed class WebBackupSettings : FingerprintHttpSettings
{
    [CommandOption("--budget <COUNT>")]
    public int Budget { get; init; } = 100;
}

public sealed class WebBackupCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace, RunCoordinator runs,
    WebMappingService web, CliOutput output) : AsyncCommand<WebBackupSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WebBackupSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var proxyPool = await ProxyCliHelpers.ResolvePoolAsync(store, settings, cancellationToken).ConfigureAwait(false);
        var run = await runs.StartAsync(project.Id, "web backups", ActivityMode.Aggressive, cancellationToken).ConfigureAwait(false);
        var findings = await web.ValidateBackupCandidatesAsync(project.Id, run.Id, new Uri(settings.Url), Math.Clamp(settings.Budget, 1, 10_000), proxyPool, cancellationToken).ConfigureAwait(false);
        await runs.CompleteAsync(run, ExecutionStatus.Completed, "complete", cancellationToken).ConfigureAwait(false);
        output.Write(findings, settings.Json, run.Id.ToString(), "complete");
        return 0;
    }
}

public class MalwareScanSettings : WorkspaceSettings
{
    [CommandOption("--artifact <ID>")]
    public Guid? ArtifactId { get; init; }
}

public sealed class MalwareScanCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    RunCoordinator runs, MalwareAnalysisService malware, CliOutput output) : AsyncCommand<MalwareScanSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, MalwareScanSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "malware scan",
            ActivityMode.Passive,
            async (_, token) =>
            {
                var projectRuns = store.Runs.Where(item => item.ProjectId == project.Id).Select(item => item.Id);
                var artifacts = await store.Artifacts
                    .Where(item => projectRuns.Contains(item.RunId) && (settings.ArtifactId == null || item.Id == settings.ArtifactId))
                    .ToListAsync(token).ConfigureAwait(false);
                var count = 0;
                foreach (var artifact in artifacts)
                {
                    count += (await malware.AnalyzeAsync(artifact, token).ConfigureAwait(false)).Count;
                }

                return new MalwareCliResult(artifacts.Count, count);
            },
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new { artifacts = execution.Result.Artifacts, observations = execution.Result.Observations },
            settings.Json,
            execution.Run.Id.ToString(),
            execution.Run.CoverageStatus);
        return 0;
    }
}

public sealed class YaraScanSettings : MalwareScanSettings
{
    [CommandArgument(0, "<RULES>")]
    public required string Rules { get; init; }
}

public sealed class YaraScanCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    RunCoordinator runs, MalwareAnalysisService malware, CliOutput output) : AsyncCommand<YaraScanSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, YaraScanSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var execution = await runs.ExecuteAsync(
            project.Id,
            "malware yara",
            ActivityMode.Passive,
            async (_, token) =>
            {
                var projectRuns = store.Runs.Where(item => item.ProjectId == project.Id).Select(item => item.Id);
                var artifacts = await store.Artifacts
                    .Where(item => projectRuns.Contains(item.RunId) && (settings.ArtifactId == null || item.Id == settings.ArtifactId))
                    .ToListAsync(token).ConfigureAwait(false);
                var results = new List<object>();
                var failed = false;
                foreach (var artifact in artifacts)
                {
                    var result = await malware.RunYaraAsync(artifact, settings.Rules, token).ConfigureAwait(false);
                    results.Add(new { artifact_id = artifact.Id, result.ExitCode, result.Output });
                    failed |= result.ExitCode > 1;
                }

                return new YaraCliResult(results, failed);
            },
            cancellationToken,
            result => result.Failed
                ? (ExecutionStatus.PartiallyFailed, "partial_scanner_failure")
                : (ExecutionStatus.Completed, "complete")).ConfigureAwait(false);
        output.Write(execution.Result.Results, settings.Json, execution.Run.Id.ToString(), execution.Run.CoverageStatus);
        return execution.Result.Failed ? 5 : 0;
    }
}

internal sealed record MalwareCliResult(int Artifacts, int Observations);

internal sealed record YaraCliResult(IReadOnlyList<object> Results, bool Failed);

public sealed class GraphExportSettings : WorkspaceSettings
{
    [CommandOption("--format <FORMAT>")]
    public string Format { get; init; } = "graphml";

    [CommandOption("--output <FILE>")]
    public string? Output { get; init; }
}

public sealed class GraphExportCommand(
    IStoreInitializer initializer, IMorsaStore store, IWorkspaceContext workspace,
    GraphExporter graph, CliOutput output) : AsyncCommand<GraphExportSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GraphExportSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var path = settings.Output ?? Path.Combine(workspace.ReportsPath, $"morsa-graph.{settings.Format}");
        await graph.ExportAsync(project.Id, settings.Format, path, cancellationToken).ConfigureAwait(false);
        output.Write(new { output = Path.GetFullPath(path), format = settings.Format }, settings.Json);
        return 0;
    }
}
