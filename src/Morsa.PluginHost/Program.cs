using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Morsa.PluginHost;

/// <summary>Bounded transparent JSONL broker for morsa-plugin/1 child processes.</summary>
public static class Program
{
    private const int MaximumLineBytes = 4 * 1024 * 1024;

    public static async Task<int> Main(string[] args)
    {
        // Managed SDK plugins are loaded only in this dedicated process, never in the Morsa CLI.
        if (args is ["--managed", var assemblyPath])
        {
            return await ManagedPluginAdapter.RunAsync(assemblyPath, MaximumLineBytes).ConfigureAwait(false);
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: morsa-plugin-host --managed <plugin-assembly> | <plugin-executable> [arguments]");
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
        foreach (var argument in args.Skip(1)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Plugin process could not start.");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var outputTask = PumpOutputAsync(process.StandardOutput, Console.Out, lifetime.Token);
        var errorTask = PumpOutputAsync(process.StandardError, Console.Error, lifetime.Token);
        try
        {
            var initialized = false;
            string? line;
            while ((line = await Console.In.ReadLineAsync(lifetime.Token).ConfigureAwait(false)) is not null)
            {
                ValidateLine(line);
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var type = document.RootElement.TryGetProperty("type", out var node) ? node.GetString() : null;
                if (!initialized)
                {
                    if (type != "initialize" || !document.RootElement.TryGetProperty("protocol", out var protocol) || protocol.GetString() != "morsa-plugin/1")
                        throw new InvalidDataException("The first plugin message must initialize morsa-plugin/1.");
                    initialized = true;
                }
                await process.StandardInput.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            TryKill(process);
            Console.Error.WriteLine("Plugin host lifetime exceeded five minutes.");
            return 9;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            TryKill(process);
            Console.Error.WriteLine(exception.Message);
            return 8;
        }
    }

    private static async Task PumpOutputAsync(StreamReader source, TextWriter destination, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await source.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            ValidateLine(line);
            await destination.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateLine(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaximumLineBytes) throw new InvalidDataException("Plugin JSONL line exceeds 4 MiB.");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Child exited concurrently. */ }
    }
}
