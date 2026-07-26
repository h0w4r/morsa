using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Metadata;

namespace Morsa.UnitTests;

[Collection("ProcessEnvironment")]
public sealed class ParserSandboxLaunchTests
{
    [Fact]
    public void BubblewrapLaunch_UsesCompatibleLimitsAndManagedHeapCeiling()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "morsa bwrap sandbox"));
        var launch = IsolatedArtifactParserGateway.BuildStartInfo(
            (Path.Combine(root, "morsa-parser-host"), null),
            Path.Combine(root, "artifact.doc"),
            "auto",
            "/usr/bin/bwrap",
            "/usr/bin/prlimit");
        var arguments = launch.StartInfo.ArgumentList.ToArray();

        Assert.Equal("/usr/bin/prlimit", launch.StartInfo.FileName);
        Assert.Contains("--cpu=60", arguments);
        Assert.Contains("--nofile=128", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--as=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--nproc=", StringComparison.Ordinal));
        Assert.Equal("0x30000000", launch.StartInfo.Environment["DOTNET_GCHeapHardLimit"]);
        Assert.Equal("0", launch.StartInfo.Environment["DOTNET_EnableDiagnostics"]);
        Assert.True(launch.IsSandboxed);
        Assert.Equal("/input/artifact", launch.ArtifactPath);
    }

    [Fact]
    public async Task ParseAsync_HostExitsBeforeProtocol_ReturnsActionableDiagnostic()
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-invalid-parser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var invalidHost = Path.Combine(root, "invalid-parser-host.dll");
        var artifactPath = Path.Combine(root, "artifact.pdf");
        await File.WriteAllTextAsync(invalidHost, "not a managed assembly");
        await File.WriteAllTextAsync(artifactPath, "%PDF-1.4\n%%EOF");
        var previousHost = Environment.GetEnvironmentVariable("MORSA_PARSER_HOST");
        var previousSandbox = Environment.GetEnvironmentVariable("MORSA_SANDBOX");
        try
        {
            Environment.SetEnvironmentVariable("MORSA_PARSER_HOST", invalidHost);
            Environment.SetEnvironmentVariable("MORSA_SANDBOX", "off");
            var gateway = new IsolatedArtifactParserGateway(new ArtifactExtractorRegistry(), new MorsaConfiguration());

            var result = await gateway.ParseAsync(
                new ArtifactContext(Guid.NewGuid(), artifactPath, new string('0', 64), ArtifactKind.Pdf, "application/pdf"),
                new ExtractionOptions(Timeout: TimeSpan.FromSeconds(15)),
                CancellationToken.None);

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("parser.process_failed", diagnostic.Code);
            Assert.True(diagnostic.IsError);
            Assert.Contains("exit code", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Broken pipe", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MORSA_PARSER_HOST", previousHost);
            Environment.SetEnvironmentVariable("MORSA_SANDBOX", previousSandbox);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OciLaunch_IsPullFreeNetworklessReadOnlyAndResourceBounded()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "morsa sandbox"));
        var host = Path.Combine(root, "morsa-parser-host");
        var artifact = Path.Combine(root, "evidence sample.pdf");

        var start = ParserSandboxCapabilities.CreateOciStartInfo(
            "/usr/bin/podman",
            host,
            null,
            artifact,
            "mcr.microsoft.com/dotnet/runtime-deps:10.0",
            "morsa-parser-unit");
        var arguments = start.ArgumentList.ToArray();

        Assert.Equal("/usr/bin/podman", start.FileName);
        Assert.Contains("--pull=never", arguments);
        Assert.Contains("--network=none", arguments);
        Assert.Contains("--read-only", arguments);
        Assert.Contains("--cap-drop=ALL", arguments);
        Assert.Contains("--security-opt=no-new-privileges", arguments);
        Assert.Contains("--security-opt=label=disable", arguments);
        Assert.Contains("--pids-limit=64", arguments);
        Assert.Contains("--memory=1073741824", arguments);
        Assert.Contains("--name=morsa-parser-unit", arguments);
        Assert.Contains($"--volume={artifact}:/input/artifact:ro", arguments);
        Assert.Contains($"--volume={root}:/host:ro", arguments);
        Assert.Equal("/host/morsa-parser-host", arguments[^1]);
    }

    [Fact]
    public void OciLaunch_UsesManagedRuntimeOnlyForDllHost()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "morsa-managed-sandbox"));
        var assembly = Path.Combine(root, "morsa-parser-host.dll");

        var start = ParserSandboxCapabilities.CreateOciStartInfo(
            "/usr/bin/docker",
            "dotnet",
            assembly,
            Path.Combine(root, "artifact.doc"),
            "mcr.microsoft.com/dotnet/runtime:10.0",
            "morsa-parser-managed-unit");
        var arguments = start.ArgumentList.ToArray();

        Assert.Equal("dotnet", arguments[^2]);
        Assert.Equal("/host/morsa-parser-host.dll", arguments[^1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains("--pull=always", StringComparison.Ordinal));
    }
}
