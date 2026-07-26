using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Application.Models;
using Morsa.Domain.Projects;
using Spectre.Console;

namespace Morsa.Cli.Runtime;

/// <summary>Writes either rich human output or a stable JSON envelope.</summary>
public sealed class CliOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public void Write<T>(T data, bool json, string? runId = null, string? coverage = null)
    {
        if (json)
        {
            var envelope = new OutputEnvelope<T>(BuildInfo.SchemaVersion, true, data, [], runId, coverage);
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }

        AnsiConsole.WriteLine(data?.ToString() ?? string.Empty);
    }

    public void WriteJsonFile<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    public static string ToJson<T>(T data) => JsonSerializer.Serialize(data, JsonOptions);
}

internal static class CommandHelpers
{
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

        return value.Count(character => character == '.') >= 1 ? "domain" : "host";
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

