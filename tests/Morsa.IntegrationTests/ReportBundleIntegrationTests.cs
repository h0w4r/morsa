using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Domain.Correlation;
using Morsa.Domain.Projects;
using Morsa.Domain.Runs;
using Morsa.Infrastructure;

namespace Morsa.IntegrationTests;

[Collection("ConsoleIsolation")]
public sealed class ReportBundleIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-report-bundle", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReportBundle_RedactedMode_IsDeterministicAndOmitsArtifactPayloads()
    {
        await SeedWorkspaceAsync();
        var first = Path.Combine(_root, "reports", "first.zip");
        var second = Path.Combine(_root, "reports", "second.zip");

        var firstRun = await RunCliAsync("report", "bundle", "--project", _root, "--output", first, "--redact", "--json");
        var secondRun = await RunCliAsync("report", "bundle", "--project", _root, "--output", second, "--redact", "--json");

        Assert.Equal(0, firstRun.ExitCode);
        Assert.Equal(0, secondRun.ExitCode);
        using (var output = JsonDocument.Parse(firstRun.StandardOutput.Trim()))
        {
            Assert.Equal("1", output.RootElement.GetProperty("schema_version").GetString());
            Assert.True(output.RootElement.GetProperty("success").GetBoolean());
        }

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first))),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(second))));
        using var archive = ZipFile.OpenRead(first);
        Assert.Equal(["evidence-manifest.json", "report.json"], archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray());
        var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "evidence-manifest.json");
        using var reader = new StreamReader(manifestEntry.Open());
        using var manifest = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.True(manifest.RootElement.GetProperty("redacted").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("artifacts")[0].GetProperty("included").GetBoolean());
        var reportEntry = Assert.Single(archive.Entries, entry => entry.FullName == "report.json");
        using var reportReader = new StreamReader(reportEntry.Open());
        var reportJson = await reportReader.ReadToEndAsync();
        Assert.DoesNotContain("HIGHLY-SECRET", reportJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/sensitive/source", reportJson, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(reportJson);
        var artifactEntity = Assert.Single(
            report.RootElement.GetProperty("entities").EnumerateArray(),
            item => item.GetProperty("type").GetString() == "artifact");
        Assert.StartsWith("[redacted:", artifactEntity.GetProperty("value").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            manifest.RootElement.GetProperty("artifacts")[0].GetProperty("sha256").GetString(),
            artifactEntity.GetProperty("normalized_value").GetString());
        Assert.All(archive.Entries, entry =>
        {
            // ZIP timestamps carry local offset, but the deterministic DOS calendar value is fixed.
            Assert.Equal(1980, entry.LastWriteTime.Year);
            Assert.Equal(1, entry.LastWriteTime.Month);
            Assert.Equal(1, entry.LastWriteTime.Day);
            Assert.Equal(0, entry.LastWriteTime.TimeOfDay.Ticks);
        });
    }

    private async Task SeedWorkspaceAsync()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var store = provider.GetRequiredService<IMorsaStore>();
        var project = new MorsaProject { Name = "bundle", RootPath = _root };
        store.Add(project);
        var run = new Run
        {
            ProjectId = project.Id,
            Command = "test report bundle",
            Mode = ActivityMode.Passive,
            Status = ExecutionStatus.Completed,
            CoverageStatus = "complete",
            StartedAt = DateTimeOffset.UnixEpoch,
            FinishedAt = DateTimeOffset.UnixEpoch,
        };
        store.Add(run);
        var storage = provider.GetRequiredService<IArtifactStorage>();
        await using var stream = new MemoryStream("HIGHLY-SECRET-PAYLOAD"u8.ToArray());
        var stored = await storage.StoreAsync(stream, "secret.txt", 4096, CancellationToken.None);
        var artifact = new Artifact
        {
            RunId = run.Id,
            StoredPath = stored.Path,
            OriginalPath = "/sensitive/source/secret.txt",
            Sha256 = stored.Sha256,
            Size = stored.Size,
            Kind = stored.Kind,
            MimeType = stored.MimeType,
        };
        store.Add(artifact);
        store.Add(new EntityNode
        {
            ProjectId = project.Id,
            Type = "artifact",
            Value = artifact.OriginalPath,
            NormalizedValue = artifact.Sha256,
            Confidence = 1.0,
        });
        store.Add(new MetadataObservation
        {
            ArtifactId = artifact.Id,
            Category = "email",
            OriginalValue = "HIGHLY-SECRET@example.test",
            NormalizedValue = "highly-secret@example.test",
            Extractor = "test",
            ExtractorVersion = "1",
            Location = "/sensitive/source/secret.txt",
        });
        await store.SaveChangesAsync();
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        using var error = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Morsa.Cli.Program.Main(arguments);
            return new CliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}

[CollectionDefinition("ConsoleIsolation", DisableParallelization = true)]
public sealed class ConsoleIsolationCollection;
