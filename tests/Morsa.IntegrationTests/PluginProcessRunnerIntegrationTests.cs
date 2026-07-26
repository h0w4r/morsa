using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Plugins;
using Morsa.PluginFixture;

namespace Morsa.IntegrationTests;

[Collection("ConsoleIsolation")]
public sealed class PluginProcessRunnerIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-plugin-runner", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_DotnetJsonlPlugin_ReturnsResultAndPersistsExecution()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var package = CreateFixturePackage();
        await provider.GetRequiredService<PluginCatalogService>().InstallAsync(package, true, CancellationToken.None);
        using var inputDocument = JsonDocument.Parse("{\"value\":42}");

        var result = await provider.GetRequiredService<PluginProcessRunner>().RunAsync(
            "fixture.jsonl", "echo", inputDocument.RootElement.Clone(), Guid.NewGuid(), TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("result", result.Response?.GetProperty("type").GetString());
        Assert.Equal("echo", result.Response?.GetProperty("operation").GetString());
        var execution = Assert.Single(provider.GetRequiredService<IMorsaStore>().PluginExecutions);
        Assert.Equal("completed", execution.Status);
        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public async Task RunAsync_ManagedSdkPlugin_ExposesManifestAndRegisteredCapabilitiesOutOfProcess()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var package = CreateFixturePackage("fixture.managed", "managed");
        await provider.GetRequiredService<PluginCatalogService>().InstallAsync(package, true, CancellationToken.None);
        using var inputDocument = JsonDocument.Parse("{}");
        var runner = provider.GetRequiredService<PluginProcessRunner>();

        // Pin the host used by the test; production resolves the sibling published executable.
        var originalHost = Environment.GetEnvironmentVariable("MORSA_PLUGIN_HOST");
        Environment.SetEnvironmentVariable("MORSA_PLUGIN_HOST", typeof(Morsa.PluginHost.Program).Assembly.Location);
        try
        {
            var manifest = await runner.RunAsync(
                "fixture.managed", "manifest", inputDocument.RootElement.Clone(), Guid.NewGuid(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var capabilities = await runner.RunAsync(
                "fixture.managed", "capabilities", inputDocument.RootElement.Clone(), Guid.NewGuid(), TimeSpan.FromSeconds(15), CancellationToken.None);
            var unsupported = await runner.RunAsync(
                "fixture.managed", "not-supported", inputDocument.RootElement.Clone(), Guid.NewGuid(), TimeSpan.FromSeconds(15), CancellationToken.None);

            Assert.Equal("fixture.managed", manifest.Response?.GetProperty("manifest").GetProperty("id").GetString());
            var capabilityNode = capabilities.Response?.GetProperty("capabilities");
            Assert.Equal("fixture.extractor", capabilityNode?.GetProperty("extractors")[0].GetProperty("id").GetString());
            Assert.Equal("text", capabilityNode?.GetProperty("extractors")[0].GetProperty("supported_kinds")[0].GetString());
            Assert.Equal("fixture.search", capabilityNode?.GetProperty("search_providers")[0].GetProperty("id").GetString());
            Assert.Equal(8, unsupported.ExitCode);
            Assert.Equal("error", unsupported.Response?.GetProperty("type").GetString());
            var executions = provider.GetRequiredService<IMorsaStore>().PluginExecutions.ToArray();
            Assert.Equal(3, executions.Length);
            Assert.Equal(2, executions.Count(execution => execution.Status == "completed"));
            var failed = Assert.Single(executions, execution => execution.Status == "failed");
            Assert.Equal("UNSUPPORTED_OPERATION", failed.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MORSA_PLUGIN_HOST", originalHost);
        }
    }

    private string CreateFixturePackage(string id = "fixture.jsonl", string kind = "dotnet-process")
    {
        var sourceAssembly = typeof(Marker).Assembly.Location;
        var sourceDirectory = Path.GetDirectoryName(sourceAssembly)!;
        var package = Path.Combine(_root, "fixture-package");
        Directory.CreateDirectory(package);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "Morsa.PluginFixture.*"))
        {
            File.Copy(source, Path.Combine(package, Path.GetFileName(source)), overwrite: true);
        }

        var manifest = new
        {
            id,
            name = kind == "managed" ? "Managed SDK fixture" : "JSONL fixture",
            version = "1.0.0",
            author = "Morsa tests",
            apiVersion = "1",
            kind,
            entryPoint = Path.GetFileName(sourceAssembly),
            arguments = Array.Empty<string>(),
            permissions = Array.Empty<string>(),
            secretEnvironmentVariables = Array.Empty<string>(),
            description = "Exercises morsa-plugin/1",
        };
        File.WriteAllText(Path.Combine(package, "morsa-plugin.json"), JsonSerializer.Serialize(manifest));
        return package;
    }
}
