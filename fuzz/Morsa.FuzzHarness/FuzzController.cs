using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Morsa.FuzzHarness;

/// <summary>Genera casos, supervisa workers y conserva reproducciones mínimas.</summary>
internal static class FuzzController
{
    private static readonly string[] ConcreteTargets =
        ["magic", "zipxml", "pdf", "svg", "rdp", "ica", "binary"];

    public static async Task<int> ExecuteAsync(FuzzOptions options)
    {
        Directory.CreateDirectory(options.OutputPath);
        var targets = options.Target == "all" ? ConcreteTargets : [options.Target];
        var dictionary = MutationEngine.LoadDictionary(options.DictionaryPath);
        var random = new Random(options.Seed);
        var stopwatch = Stopwatch.StartNew();
        var totalExecutions = 0;
        var findings = 0;

        foreach (var target in targets)
        {
            var seeds = LoadSeeds(options.CorpusPath, target, options.MaxInputBytes);
            if (seeds.Count == 0)
            {
                throw new ArgumentException($"Target '{target}' has no seed files.");
            }

            var targetIterations = options.SeedOnly ? seeds.Count : options.Iterations;
            var mutator = new MutationEngine(unchecked(options.Seed ^ StableTargetHash(target)), dictionary,
                options.MaxInputBytes);

            for (var iteration = 0; iteration < targetIterations; iteration++)
            {
                if (stopwatch.Elapsed >= TimeSpan.FromSeconds(options.MaxTotalSeconds))
                {
                    PrintSummary(options, totalExecutions, findings, stopwatch.Elapsed, budgetExhausted: true);
                    return findings == 0 ? ExitCodes.Success : ExitCodes.FindingDetected;
                }

                var seed = options.SeedOnly ? seeds[iteration] : seeds[random.Next(seeds.Count)];
                var bytes = options.SeedOnly ? seed.Content : mutator.Mutate(seed.Content, seeds.Select(item => item.Content).ToArray());
                var execution = await ExecuteCaseAsync(options, target, iteration, seed.Path, bytes).ConfigureAwait(false);
                totalExecutions++;
                if (execution.IsFinding)
                {
                    findings++;
                    await PreserveFindingAsync(options, target, iteration, seed.Path, bytes, execution)
                        .ConfigureAwait(false);
                    if (options.StopOnFinding)
                    {
                        PrintSummary(options, totalExecutions, findings, stopwatch.Elapsed, budgetExhausted: false);
                        return ExitCodes.FindingDetected;
                    }
                }
            }
        }

        PrintSummary(options, totalExecutions, findings, stopwatch.Elapsed, budgetExhausted: false);
        return findings == 0 ? ExitCodes.Success : ExitCodes.FindingDetected;
    }

    private static List<SeedInput> LoadSeeds(string corpusRoot, string target, int maximumBytes)
    {
        var directory = Path.Combine(corpusRoot, target);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .Where(info => info.Length <= maximumBytes)
            .Select(info => new SeedInput(info.FullName, File.ReadAllBytes(info.FullName)))
            .ToList();
    }

    private static async Task<ExecutionResult> ExecuteCaseAsync(
        FuzzOptions options,
        string target,
        int iteration,
        string seedPath,
        byte[] bytes)
    {
        var temporaryDirectory = Path.Combine(options.OutputPath, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var inputPath = Path.Combine(temporaryDirectory,
            $"{Environment.ProcessId}-{target}-{iteration:D8}{ExtensionFor(target)}");
        await File.WriteAllBytesAsync(inputPath, bytes).ConfigureAwait(false);

        try
        {
            using var process = new Process { StartInfo = CreateWorkerStartInfo(options, target, inputPath) };
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(options.TimeoutMilliseconds + 500);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new ExecutionResult(ExitCodes.Timeout, true, true, string.Empty,
                    "worker_timeout: external watchdog killed process", seedPath);
            }

            var output = await standardOutput.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var error = await standardError.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            // Cualquier salida no nula del worker indica crash, señal nativa o fallo del contrato.
            var finding = process.ExitCode != ExitCodes.Success;
            return new ExecutionResult(process.ExitCode, finding, process.ExitCode == ExitCodes.Timeout,
                Limit(output), Limit(error), seedPath);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(FuzzOptions options, string target, string inputPath)
    {
        // AppContext funciona tanto en despliegues framework-dependent como single-file.
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Morsa.FuzzHarness.dll");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve current process.");
        var usingDotnetHost = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (usingDotnetHost)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--timeout-ms");
        startInfo.ArgumentList.Add(options.TimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--max-input-bytes");
        startInfo.ArgumentList.Add(options.MaxInputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        return startInfo;
    }

    private static async Task PreserveFindingAsync(
        FuzzOptions options,
        string target,
        int iteration,
        string seedPath,
        byte[] bytes,
        ExecutionResult execution)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var findingDirectory = Path.Combine(options.OutputPath, "findings", target, hash[..16]);
        Directory.CreateDirectory(findingDirectory);
        var samplePath = Path.Combine(findingDirectory, $"input{ExtensionFor(target)}");
        await File.WriteAllBytesAsync(samplePath, bytes).ConfigureAwait(false);

        var manifest = new
        {
            schema_version = "morsa-fuzz-finding/1",
            target,
            iteration,
            controller_seed = options.Seed,
            seed_file = Path.GetRelativePath(options.CorpusPath, seedPath).Replace('\\', '/'),
            sha256 = hash,
            size = bytes.Length,
            exit_code = execution.ExitCode,
            timeout = execution.TimedOut,
            stdout = execution.StandardOutput,
            stderr = execution.StandardError,
            reproduce = $"./fuzz/scripts/reproduce.sh {target} '{samplePath}'",
            recorded_at_utc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(findingDirectory, "finding.json"), json).ConfigureAwait(false);
        Console.Error.WriteLine($"finding target={target} sha256={hash} path={samplePath}");
    }

    private static void PrintSummary(
        FuzzOptions options,
        int executions,
        int findings,
        TimeSpan elapsed,
        bool budgetExhausted)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema_version = "morsa-fuzz-summary/1",
            target = options.Target,
            seed = options.Seed,
            seed_only = options.SeedOnly,
            executions,
            findings,
            elapsed_ms = (long)elapsed.TotalMilliseconds,
            total_time_budget_exhausted = budgetExhausted,
        }));
    }

    private static int StableTargetHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
            {
                hash = (hash * 31) + character;
            }

            return hash;
        }
    }

    private static string ExtensionFor(string target) => target switch
    {
        "zipxml" => ".zip",
        "pdf" => ".pdf",
        "svg" => ".svg",
        "rdp" => ".rdp",
        "ica" => ".ica",
        _ => ".bin",
    };

    private static string Limit(string value) => value.Length <= 8_192 ? value : value[..8_192];

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
        }
        catch (InvalidOperationException)
        {
            // El proceso terminó entre la comprobación y el intento de kill.
        }
    }

    private sealed record SeedInput(string Path, byte[] Content);

    private sealed record ExecutionResult(
        int ExitCode,
        bool IsFinding,
        bool TimedOut,
        string StandardOutput,
        string StandardError,
        string SeedPath);
}
