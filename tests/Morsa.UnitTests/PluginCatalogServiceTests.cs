using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Morsa.Infrastructure.Plugins;
using Morsa.Infrastructure.Workspace;

namespace Morsa.UnitTests;

public sealed class PluginCatalogServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-plugin-catalog", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_ValidManifestAndChecksum_InstallsAndActivatesVersion()
    {
        var source = CreatePackage("fixture.echo", "1.0.0", "echo.bin", "fixture payload");
        var service = new PluginCatalogService(new WorkspaceContext(_root));

        var installed = await service.InstallAsync(source, activate: true, CancellationToken.None);
        var current = await service.GetCurrentAsync("fixture.echo", CancellationToken.None);

        Assert.True(installed.IsValid);
        Assert.True(installed.IsCurrent);
        Assert.Equal("1.0.0", current.Manifest.Version);
        Assert.True(File.Exists(Path.Combine(current.Directory, "echo.bin")));
    }

    [Fact]
    public async Task InstallAsync_ZipPathTraversal_RejectsAndDoesNotEscapeStaging()
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, "traversal.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
            writer.Write("must not escape");
        }

        var service = new PluginCatalogService(new WorkspaceContext(_root));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(zipPath, activate: true, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(service.RootPath, ".staging", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(service.RootPath, "escaped.txt")));
    }

    [Fact]
    public async Task InstallAsync_EntryPointTraversalOrUnknownPermission_RejectsManifest()
    {
        var source = CreatePackage("fixture.invalid", "1.0.0", "entry.bin", "payload");
        WriteManifest(source, "fixture.invalid", "1.0.0", "../entry.bin", null, ["root:everything"]);
        var service = new PluginCatalogService(new WorkspaceContext(_root));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(source, activate: false, CancellationToken.None));
        Assert.Empty(await service.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RollbackAsync_TwoInstalledVersions_ActivatesPreviousVersionAtomically()
    {
        var service = new PluginCatalogService(new WorkspaceContext(_root));
        await service.InstallAsync(CreatePackage("fixture.rollback", "1.0.0", "entry.bin", "v1"), true, CancellationToken.None);
        await service.InstallAsync(CreatePackage("fixture.rollback", "2.0.0", "entry.bin", "v2"), true, CancellationToken.None);

        var version = await service.RollbackAsync("fixture.rollback", CancellationToken.None);
        var current = await service.GetCurrentAsync("fixture.rollback", CancellationToken.None);

        Assert.Equal("1.0.0", version);
        Assert.Equal("1.0.0", current.Manifest.Version);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(current.Directory, "entry.bin")));
    }

    private string CreatePackage(string id, string version, string entryPoint, string content)
    {
        var source = Path.Combine(_root, "sources", id, version);
        Directory.CreateDirectory(source);
        var entry = Path.Combine(source, entryPoint);
        File.WriteAllText(entry, content);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))).ToLowerInvariant();
        WriteManifest(source, id, version, entryPoint, hash, []);
        return source;
    }

    private static void WriteManifest(
        string directory,
        string id,
        string version,
        string entryPoint,
        string? sha256,
        string[] permissions)
    {
        var manifest = new
        {
            id,
            name = id,
            version,
            author = "Morsa tests",
            apiVersion = "1",
            kind = "dotnet-process",
            entryPoint,
            arguments = Array.Empty<string>(),
            permissions,
            secretEnvironmentVariables = Array.Empty<string>(),
            sha256,
            description = "Synthetic plugin fixture",
        };
        File.WriteAllText(Path.Combine(directory, "morsa-plugin.json"), JsonSerializer.Serialize(manifest));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
