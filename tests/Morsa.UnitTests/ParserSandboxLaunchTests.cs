using Morsa.Infrastructure.Metadata;

namespace Morsa.UnitTests;

public sealed class ParserSandboxLaunchTests
{
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
