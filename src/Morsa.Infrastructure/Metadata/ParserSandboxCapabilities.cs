using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Describes a locally usable OCI parser boundary without pulling an image implicitly.</summary>
public sealed record ParserOciCapability(string? Engine, string Image, bool ImageAvailable);

/// <summary>Probes and composes the optional Podman/Docker parser sandbox.</summary>
public static class ParserSandboxCapabilities
{
    private const int ProbeTimeoutMilliseconds = 5_000;

    /// <summary>Finds a local engine and verifies that the required image is already present.</summary>
    public static ParserOciCapability ProbeOci(bool managedHost)
    {
        var image = ResolveImage(managedHost);
        if (!OperatingSystem.IsLinux() || !IsSafeImageReference(image))
            return new(null, IsSafeImageReference(image) ? image : "invalid", false);

        var engine = FindOnPath("podman") ?? FindOnPath("docker");
        return new(engine, image, engine is not null && IsImageAvailable(engine, image));
    }

    /// <summary>Builds a pull-free, networkless and read-only OCI invocation for one artifact.</summary>
    internal static ProcessStartInfo CreateOciStartInfo(
        string engine,
        string hostExecutable,
        string? hostAssembly,
        string artifactPath,
        string image,
        string containerName)
    {
        var hostPayload = Path.GetFullPath(hostAssembly ?? hostExecutable);
        var hostDirectory = Path.GetDirectoryName(hostPayload) ??
                            throw new InvalidOperationException("ParserHost directory could not be resolved.");
        var start = new ProcessStartInfo(engine) { UseShellExecute = false };

        // --pull=never prevents a parser request from causing undeclared network traffic.
        foreach (var argument in new[]
                 {
                     "run", "--rm", "--interactive", "--pull=never", "--network=none", "--read-only",
                     "--cap-drop=ALL", "--security-opt=no-new-privileges", "--security-opt=label=disable", "--pids-limit=64",
                     "--memory=1073741824", "--cpus=1.0", "--tmpfs=/tmp:rw,nosuid,nodev,size=268435456",
                     $"--name={containerName}",
                     $"--volume={hostDirectory}:/host:ro", $"--volume={artifactPath}:/input/artifact:ro",
                     "--workdir=/tmp", "--env=HOME=/tmp", "--env=TMPDIR=/tmp", "--env=DOTNET_CLI_HOME=/tmp",
                     "--env=LANG=C.UTF-8", image,
                 })
            start.ArgumentList.Add(argument);

        if (hostAssembly is null)
        {
            start.ArgumentList.Add($"/host/{Path.GetFileName(hostPayload)}");
        }
        else
        {
            start.ArgumentList.Add("dotnet");
            start.ArgumentList.Add($"/host/{Path.GetFileName(hostPayload)}");
        }

        return start;
    }

    private static string ResolveImage(bool managedHost)
    {
        var configured = Environment.GetEnvironmentVariable("MORSA_PARSER_OCI_IMAGE");
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var musl = RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase);
        if (managedHost)
            return musl ? "mcr.microsoft.com/dotnet/runtime:10.0-alpine" : "mcr.microsoft.com/dotnet/runtime:10.0";
        return musl ? "mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine" : "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    }

    private static bool IsSafeImageReference(string image) =>
        image.Length is > 0 and <= 256 &&
        image[0] != '-' &&
        !image.Contains("://", StringComparison.Ordinal) &&
        image.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));

    private static bool IsImageAvailable(string engine, string image)
    {
        var isPodman = Path.GetFileName(engine).StartsWith("podman", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (isPodman)
        {
            start.ArgumentList.Add("image");
            start.ArgumentList.Add("exists");
        }
        else
        {
            start.ArgumentList.Add("image");
            start.ArgumentList.Add("inspect");
            start.ArgumentList.Add("--format={{.Id}}");
        }
        start.ArgumentList.Add(image);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }
            // Complete both drains before disposing the process; their content is intentionally discarded.
            Task.WaitAll([output, error], ProbeTimeoutMilliseconds);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException or
            AggregateException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
