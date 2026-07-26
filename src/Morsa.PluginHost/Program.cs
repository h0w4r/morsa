using System.Diagnostics;
using System.Text.Json;

namespace Morsa.PluginHost;

/// <summary>Bounded JSONL bridge for external morsa-plugin/1 processes.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: morsa-plugin-host <plugin-executable> [arguments]");
            return 2;
        }

        var start = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in args.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Plugin process could not start.");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "initialize",
            protocol = "morsa-plugin/1",
        })).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var response = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (response is null)
        {
            process.Kill(entireProcessTree: true);
            return 9;
        }

        await Console.Out.WriteLineAsync(response).ConfigureAwait(false);
        process.Kill(entireProcessTree: true);
        return 0;
    }
}

