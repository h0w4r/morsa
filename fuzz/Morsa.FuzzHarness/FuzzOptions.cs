using System.Globalization;

namespace Morsa.FuzzHarness;

/// <summary>Opciones inmutables y validadas del controlador y del worker.</summary>
internal sealed record FuzzOptions
{
    private static readonly HashSet<string> ValidTargets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "all", "magic", "zipxml", "pdf", "svg", "rdp", "ica", "binary",
        };

    private static readonly HashSet<string> ValueOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "--target", "--corpus", "--output", "--dictionary", "--iterations", "--timeout-ms",
            "--max-input-bytes", "--max-total-seconds", "--seed", "--input",
        };

    public string Target { get; init; } = "all";

    public string CorpusPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public string? DictionaryPath { get; init; }

    public int Iterations { get; init; } = 1_000;

    public int TimeoutMilliseconds { get; init; } = 2_000;

    public int MaxInputBytes { get; init; } = 1 * 1024 * 1024;

    public int MaxTotalSeconds { get; init; } = 300;

    public int Seed { get; init; } = 0x4D4F5253;

    public bool SeedOnly { get; init; }

    public bool StopOnFinding { get; init; }

    public bool WorkerMode { get; init; }

    public string? InputPath { get; init; }

    public static FuzzOptions Parse(string[] args)
    {
        var defaults = DiscoverDefaults();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--worker" or "--seed-only" or "--stop-on-finding")
            {
                switches.Add(argument);
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected positional argument '{argument}'.");
            }

            if (!ValueOptions.Contains(argument))
            {
                throw new ArgumentException($"Unknown option '{argument}'.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            values[argument] = args[++index];
        }

        var options = new FuzzOptions
        {
            Target = Value(values, "--target", "all").ToLowerInvariant(),
            CorpusPath = Path.GetFullPath(Value(values, "--corpus", defaults.Corpus)),
            OutputPath = Path.GetFullPath(Value(values, "--output", defaults.Output)),
            DictionaryPath = ResolveDictionary(values, defaults.Dictionary),
            Iterations = Integer(values, "--iterations", 1_000, 0, 10_000_000),
            TimeoutMilliseconds = Integer(values, "--timeout-ms", 2_000, 100, 300_000),
            MaxInputBytes = Integer(values, "--max-input-bytes", 1 * 1024 * 1024, 64, 64 * 1024 * 1024),
            MaxTotalSeconds = Integer(values, "--max-total-seconds", 300, 1, 86_400),
            Seed = Integer(values, "--seed", 0x4D4F5253, int.MinValue, int.MaxValue),
            InputPath = values.TryGetValue("--input", out var input) ? Path.GetFullPath(input) : null,
            SeedOnly = switches.Contains("--seed-only"),
            StopOnFinding = switches.Contains("--stop-on-finding"),
            WorkerMode = switches.Contains("--worker"),
        };

        options.Validate();
        return options;
    }

    public static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: Morsa.FuzzHarness --target <all|magic|zipxml|pdf|svg|rdp|ica|binary> " +
            "[--corpus PATH] [--dictionary PATH] [--output PATH] [--iterations N] " +
            "[--timeout-ms N] [--max-input-bytes N] [--max-total-seconds N] " +
            "[--seed N] [--seed-only] [--stop-on-finding]");
    }

    private void Validate()
    {
        if (!ValidTargets.Contains(Target))
        {
            throw new ArgumentException($"Unknown target '{Target}'.");
        }

        if (WorkerMode)
        {
            if (Target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Worker mode requires one concrete target.");
            }

            if (string.IsNullOrWhiteSpace(InputPath))
            {
                throw new ArgumentException("Worker mode requires --input.");
            }

            return;
        }

        if (!Directory.Exists(CorpusPath))
        {
            throw new ArgumentException($"Corpus directory does not exist: {CorpusPath}");
        }

        if (DictionaryPath is not null && !File.Exists(DictionaryPath))
        {
            throw new ArgumentException($"Dictionary does not exist: {DictionaryPath}");
        }
    }

    private static (string Corpus, string Dictionary, string Output) DiscoverDefaults()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var corpus = Path.Combine(current.FullName, "fuzz", "corpus");
            if (Directory.Exists(corpus))
            {
                return (
                    corpus,
                    Path.Combine(current.FullName, "fuzz", "dictionaries", "morsa.dict"),
                    Path.Combine(current.FullName, "fuzz", "artifacts"));
            }

            current = current.Parent;
        }

        var working = Directory.GetCurrentDirectory();
        return (
            Path.Combine(working, "fuzz", "corpus"),
            Path.Combine(working, "fuzz", "dictionaries", "morsa.dict"),
            Path.Combine(working, "fuzz", "artifacts"));
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static string? ResolveDictionary(IReadOnlyDictionary<string, string> values, string fallback)
    {
        if (values.TryGetValue("--dictionary", out var explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        return File.Exists(fallback) ? Path.GetFullPath(fallback) : null;
    }

    private static int Integer(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            throw new ArgumentException($"Option '{key}' must be an integer between {minimum} and {maximum}.");
        }

        return value;
    }
}
