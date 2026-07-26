using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Models;
using Morsa.Domain.Projects;
using Morsa.Infrastructure.Configuration;
using Spectre.Console;

namespace Morsa.Cli.Runtime;

/// <summary>Writes either rich human output or a stable JSON envelope.</summary>
public sealed partial class CliOutput(MorsaConfiguration? configuration = null, bool ndjsonRequested = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public bool MachineReadable => ndjsonRequested || configuration?.Output.Format is "json" or "ndjson";

    public void Write<T>(T data, bool json, string? runId = null, string? coverage = null)
    {
        var configuredFormat = configuration?.Output.Format ?? "table";
        var machine = json || configuredFormat is "json" or "ndjson";
        var ndjson = ndjsonRequested || configuredFormat == "ndjson";
        if (machine)
        {
            if (ndjson && data is System.Collections.IEnumerable sequence && data is not string && data is not JsonElement && data is not System.Collections.IDictionary)
            {
                var emitted = false;
                foreach (var item in sequence)
                {
                    emitted = true;
                    var itemEnvelope = new OutputEnvelope<object?>(BuildInfo.SchemaVersion, true, item, [], runId, coverage);
                    Console.Out.WriteLine(JsonSerializer.Serialize(itemEnvelope, JsonOptions));
                }
                if (emitted) return;
            }

            var envelope = new OutputEnvelope<T>(BuildInfo.SchemaVersion, true, data, [], runId, coverage);
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }

        AnsiConsole.WriteLine(data?.ToString() ?? string.Empty);
    }

    public void WriteJsonFile<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(data, options));
    }

    /// <summary>Emits a stable machine-readable failure without leaking stack traces.</summary>
    public void WriteError(string code, string message)
    {
        var envelope = new OutputEnvelope<object>(BuildInfo.SchemaVersion, false, null, [new OperationError(code, SanitizeDiagnostic(message))]);
        Console.Out.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    /// <summary>Bounds diagnostics and removes URI credentials/query strings plus known secret environment values.</summary>
    public static string SanitizeDiagnostic(string message)
    {
        var sanitized = UriDiagnosticRegex().Replace(message, match =>
        {
            if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ')'), UriKind.Absolute, out var uri)) return "[redacted-uri]";
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
        });
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = variable.Key?.ToString() ?? string.Empty;
            var value = variable.Value?.ToString();
            if (value is { Length: >= 4 } && new[] { "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD" }
                    .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)))
                sanitized = sanitized.Replace(value, "[redacted]", StringComparison.Ordinal);
        }
        return sanitized.Length <= 2048 ? sanitized : sanitized[..2048] + " [truncated]";
    }

    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UriDiagnosticRegex();

    public static string ToJson<T>(T data) => JsonSerializer.Serialize(data, JsonOptions);
}

internal static class CommandHelpers
{
    /// <summary>Converts CLI MiB input without integer overflow or unbounded allocations.</summary>
    public static int ToIntByteBudget(int megabytes, int maximumMegabytes = 2047)
    {
        if (megabytes < 1 || megabytes > maximumMegabytes)
            throw new ArgumentOutOfRangeException(nameof(megabytes), $"Byte budget must be between 1 and {maximumMegabytes} MiB.");
        return checked(megabytes * 1024 * 1024);
    }

    /// <summary>Converts larger file budgets using checked 64-bit arithmetic.</summary>
    public static long ToLongByteBudget(int megabytes, int maximumMegabytes = 102_400)
    {
        if (megabytes < 1 || megabytes > maximumMegabytes)
            throw new ArgumentOutOfRangeException(nameof(megabytes), $"Byte budget must be between 1 and {maximumMegabytes} MiB.");
        return checked(megabytes * 1024L * 1024L);
    }

    public static async Task<MorsaProject> RequireProjectAsync(
        IStoreInitializer initializer,
        IMorsaStore store,
        IWorkspaceContext workspace,
        CancellationToken cancellationToken)
    {
        await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await store.Projects.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ??
               throw new InvalidOperationException($"No Morsa project exists in '{workspace.RootPath}'. Run 'morsa init'.");
    }

    public static string InferScopeKind(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return "url";
        }

        if (System.Net.IPAddress.TryParse(value, out _))
        {
            return "ip";
        }

        if (value.Contains('/') && System.Net.IPAddress.TryParse(value.Split('/', 2)[0], out _)) return "cidr";

        return value.Count(character => character == '.') >= 1 ? "domain" : "host";
    }

    public static string NormalizeScopeValue(string value, string kind)
    {
        var trimmed = value.Trim();
        return kind switch
        {
            "url" when Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" =>
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
            "domain" or "host" => new System.Globalization.IdnMapping().GetAscii(trimmed.TrimEnd('.')).ToLowerInvariant(),
            "ip" when System.Net.IPAddress.TryParse(trimmed, out var address) => address.ToString(),
            "cidr" => NormalizeCidr(trimmed),
            "url" => throw new InvalidDataException("URL scope must be an absolute HTTP or HTTPS URI."),
            "ip" => throw new InvalidDataException("IP scope must be a valid IPv4 or IPv6 address."),
            _ => throw new InvalidDataException("Scope kind must be domain, host, url, ip or cidr."),
        };
    }

    private static string NormalizeCidr(string value)
    {
        var parts = value.Split('/', 2);
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > address.GetAddressBytes().Length * 8)
            throw new InvalidDataException("CIDR scope is invalid.");
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        return $"{new System.Net.IPAddress(bytes)}/{prefix}";
    }
}

internal static class ArgumentPreParser
{
    public static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                return args[index + 1];
            }

            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(name.Length + 1)..];
            }
        }

        return null;
    }
}
