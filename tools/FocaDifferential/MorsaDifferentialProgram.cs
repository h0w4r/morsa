// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

const string BaselineCommit = "754453ad7f9579a6021c484d5014a3cd12fd0e35";
if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: morsa-foca-differential <FocaRunner.exe> [report.json]");
    return 2;
}

var focaRunner = Path.GetFullPath(args[0]);
if (!File.Exists(focaRunner)) throw new FileNotFoundException("FOCA differential runner was not found.", focaRunner);

using var corpus = new Morsa.UnitTests.LegacySyntheticCorpus();
var artifacts = new[]
{
    corpus.CreateOleCompound(),
    corpus.CreateInDesign(),
    corpus.CreateWordPerfect(),
    corpus.CreatePdf(),
};
var inspector = new MagicByteArtifactInspector();
var registry = new ArtifactExtractorRegistry();
var results = new List<object>();
var failures = new List<string>();

foreach (var path in artifacts)
{
    var foca = await RunFocaAsync(focaRunner, path);
    var inspected = await inspector.InspectAsync(path, CancellationToken.None);
    var extractor = registry.Select(inspected.Kind) ?? throw new InvalidOperationException($"Morsa has no extractor for {inspected.Kind}.");
    var extraction = await extractor.ExtractAsync(
        new ArtifactContext(Guid.NewGuid(), path, "synthetic", inspected.Kind, inspected.MimeType),
        new ExtractionOptions(),
        CancellationToken.None);
    var morsa = Summarize(extraction);
    var missing = foca.Where(item => item.Value > 0 && (!morsa.TryGetValue(item.Key, out var count) || count == 0))
        .Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
    if (extraction.Observations.Count == 0) missing = [.. missing, "all_morsa_observations"];
    if (missing.Length > 0) failures.Add($"{Path.GetFileName(path)}: {string.Join(',', missing)}");
    results.Add(new { file = Path.GetFileName(path), kind = inspected.Kind.ToString(), foca, morsa, missing });
}

var report = new
{
    schema_version = "1",
    baseline = new { repository = "https://github.com/ElevenPaths/FOCA", commit = BaselineCommit },
    corpus = "deterministic-synthetic",
    files = results,
    success = failures.Count == 0,
    failures,
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
if (args.Length == 2)
{
    var output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, json);
}
Console.WriteLine(json);
return failures.Count == 0 ? 0 : 1;

static async Task<Dictionary<string, int>> RunFocaAsync(string runner, string artifact)
{
    var start = new ProcessStartInfo(runner) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(artifact);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("FOCA runner did not start.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await process.WaitForExitAsync(timeout.Token);
    var output = await outputTask;
    var error = await errorTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"FOCA failed for {Path.GetFileName(artifact)}: {error}");
    return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('=', 2))
        .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
        .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
}

static Dictionary<string, int> Summarize(ExtractionResult result)
{
    var categories = result.Observations.GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    int Count(params string[] names) => names.Sum(name => categories.GetValueOrDefault(name));
    return new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["users"] = Count("author", "last_saved_by", "username", "manager", "history.author"),
        ["applications"] = Count("application"),
        ["emails"] = Count("email"),
        ["paths"] = Count("path", "unc_path", "url"),
        ["servers"] = Count("server", "hostname"),
        ["printers"] = Count("printer"),
        ["password_indicators"] = Count("password", "credential", "credential_indicator"),
        ["history"] = categories.Where(item => item.Key.StartsWith("history", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Value),
        ["old_versions"] = Count("old_version"),
        ["title"] = Count("title"),
        ["company"] = Count("company"),
        ["operating_system"] = Count("operating_system"),
        ["gps"] = Count("gps"),
    };
}
