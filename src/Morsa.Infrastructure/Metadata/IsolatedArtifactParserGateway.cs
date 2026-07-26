using System.Diagnostics;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Configuration;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Uses ParserHost with bounded JSONL and Bubblewrap/OCI isolation when locally available.</summary>
public sealed class IsolatedArtifactParserGateway(
    IArtifactExtractorRegistry registry,
    MorsaConfiguration configuration) : IArtifactParserGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ExtractionResult> ParseAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.MaxBytes <= 0 || options.MaxUncompressedBytes <= 0 ||
            options.MaxContainerEntries is < 1 or > 100_000 || options.MaxDepth is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Parser budgets must be positive and within hard safety ceilings.");
        // Environment remains the explicit per-process override; otherwise the project/global TOML policy is authoritative.
        var mode = (Environment.GetEnvironmentVariable("MORSA_SANDBOX") ?? configuration.Artifacts.Sandbox).ToLowerInvariant();
        var fullArtifactPath = Path.GetFullPath(artifact.Path);
        if (!File.Exists(fullArtifactPath)) throw new FileNotFoundException("Artifact does not exist.", fullArtifactPath);
        if ((File.GetAttributes(fullArtifactPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Parser input must not be a symbolic link or reparse point.");
        var host = ResolveHost();
        if (host is null)
        {
            if (mode == "strict") throw new InvalidOperationException("Strict parser sandbox requested, but Morsa.ParserHost is unavailable.");
            return await ParseInProcessAsync(artifact, options, cancellationToken).ConfigureAwait(false);
        }

        var launch = BuildStartInfo(host.Value, fullArtifactPath, mode);
        using var process = Process.Start(launch.StartInfo) ?? throw new InvalidOperationException("ParserHost could not start.");
        var timeout = options.Timeout ?? TimeSpan.FromSeconds(30);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var requestArtifact = artifact with { Path = launch.ArtifactPath };
            var request = JsonSerializer.Serialize(new ParserRequest(Guid.NewGuid().ToString("N"), requestArtifact, options), JsonOptions);
            // Drain stderr concurrently so parser diagnostics cannot block the protocol pipe.
            var errorTask = ReadBoundedAsync(process.StandardError, 1024 * 1024, deadline.Token);
            await process.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            var line = await ReadBoundedLineAsync(process.StandardOutput, 16 * 1024 * 1024, deadline.Token).ConfigureAwait(false) ??
                       throw new InvalidDataException("ParserHost returned no response.");
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("type").GetString() == "error")
            {
                var message = document.RootElement.TryGetProperty("message", out var node) ? node.GetString() : error;
                return new ExtractionResult([], [], [new("parser.failed", message ?? "ParserHost failed.", true)]);
            }

            var result = document.RootElement.GetProperty("result").Deserialize<ExtractionResult>(JsonOptions) ??
                         throw new InvalidDataException("ParserHost response does not contain an extraction result.");
            return launch.IsSandboxed
                ? result
                : result with { Diagnostics = [.. result.Diagnostics, new("sandbox.degraded", "ParserHost ran as a bounded subprocess without an OS filesystem sandbox.", false)] };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process, launch);
            return new ExtractionResult([], [], [new("parser.timeout", $"Parser exceeded {timeout}.", true)]);
        }
        catch
        {
            TryKill(process, launch);
            throw;
        }
    }

    private async Task<ExtractionResult> ParseInProcessAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var extractor = registry.Select(artifact.Kind);
        if (extractor is null)
        {
            return new ExtractionResult([], [], [new("artifact.unsupported", $"No extractor supports {artifact.Kind}.", true)]);
        }

        var result = await extractor.ExtractAsync(artifact, options, cancellationToken).ConfigureAwait(false);
        return new ExtractionResult(
            result.Observations,
            result.Findings,
            [.. result.Diagnostics, new("sandbox.degraded", "ParserHost unavailable; auto sandbox used the restricted application process.", false)]);
    }

    private static (string FileName, string? Assembly)? ResolveHost()
    {
        var configured = Environment.GetEnvironmentVariable("MORSA_PARSER_HOST");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetExtension(configured).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", configured)
                : (configured, null);
        }

        var executableName = OperatingSystem.IsWindows() ? "morsa-parser-host.exe" : "morsa-parser-host";
        var executable = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(executable)) return (executable, null);
        if (OperatingSystem.IsLinux())
        {
            foreach (var standardPath in new[] { "/usr/libexec/morsa/morsa-parser-host", "/usr/local/libexec/morsa/morsa-parser-host", "/opt/morsa/libexec/morsa-parser-host" })
                if (File.Exists(standardPath)) return (standardPath, null);
        }
        var assembly = new[] { "morsa-parser-host.dll", "Morsa.ParserHost.dll" }
            .Select(name => Path.Combine(AppContext.BaseDirectory, name)).FirstOrDefault(File.Exists);
        if (assembly is not null) return ("dotnet", assembly);

        // Developer builds keep executable projects in sibling output directories.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            foreach (var name in new[] { "morsa-parser-host.dll", "Morsa.ParserHost.dll" })
            {
                var candidate = Path.Combine(directory.FullName, "src", "Morsa.ParserHost", "bin", "Release", "net10.0", name);
                if (File.Exists(candidate)) return ("dotnet", candidate);
            }
        }
        return null;
    }

    private static ParserLaunch BuildStartInfo((string FileName, string? Assembly) host, string artifactPath, string mode)
    {
        var bubblewrap = mode == "off" ? null : OperatingSystem.IsLinux() ? FindOnPath("bwrap") : null;
        ProcessStartInfo start;
        var sandboxKind = "process";
        var requestArtifactPath = artifactPath;
        string? ociEngine = null;
        string? ociContainerName = null;
        if (bubblewrap is not null)
        {
            sandboxKind = "bubblewrap";
            requestArtifactPath = "/input/artifact";
            var limiter = FindOnPath("prlimit");
            start = new ProcessStartInfo(limiter ?? bubblewrap) { UseShellExecute = false };
            if (limiter is not null)
            {
                foreach (var argument in new[] { "--as=1073741824", "--cpu=60", "--nproc=64", "--nofile=128", "--", bubblewrap })
                    start.ArgumentList.Add(argument);
            }

            // Expose only runtime files, ParserHost and the single artifact; never bind the user's whole root.
            foreach (var argument in new[] { "--die-with-parent", "--new-session", "--unshare-net", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--cap-drop", "ALL", "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--dir", "/input", "--dir", "/host" })
                start.ArgumentList.Add(argument);
            foreach (var systemPath in new[] { "/usr", "/bin", "/lib", "/lib64", "/etc/ssl/certs", "/etc/ld.so.cache" }.Where(path => File.Exists(path) || Directory.Exists(path)))
            {
                start.ArgumentList.Add("--ro-bind");
                start.ArgumentList.Add(systemPath);
                start.ArgumentList.Add(systemPath);
            }

            var hostPayload = host.Assembly is null ? Path.GetFullPath(host.FileName) : Path.GetFullPath(host.Assembly);
            var hostDirectory = Path.GetDirectoryName(hostPayload)!;
            start.ArgumentList.Add("--ro-bind");
            start.ArgumentList.Add(hostDirectory);
            start.ArgumentList.Add("/host");
            start.ArgumentList.Add("--ro-bind");
            start.ArgumentList.Add(artifactPath);
            start.ArgumentList.Add("/input/artifact");
            start.ArgumentList.Add("--chdir");
            start.ArgumentList.Add("/tmp");
            start.ArgumentList.Add(host.Assembly is null ? $"/host/{Path.GetFileName(hostPayload)}" : "dotnet");
            if (host.Assembly is not null) start.ArgumentList.Add($"/host/{Path.GetFileName(hostPayload)}");
        }
        else
        {
            var oci = mode == "off" ? new ParserOciCapability(null, "disabled", false) :
                ParserSandboxCapabilities.ProbeOci(host.Assembly is not null);
            if (oci is { Engine: not null, ImageAvailable: true })
            {
                sandboxKind = "oci";
                requestArtifactPath = "/input/artifact";
                ociEngine = oci.Engine;
                ociContainerName = $"morsa-parser-{Guid.NewGuid():N}";
                start = ParserSandboxCapabilities.CreateOciStartInfo(
                    oci.Engine,
                    host.FileName,
                    host.Assembly,
                    artifactPath,
                    oci.Image,
                    ociContainerName);
            }
            else
            {
                if (mode == "strict")
                    throw new InvalidOperationException("Strict parser sandbox requires Bubblewrap or a locally cached OCI runtime image on Linux.");
                start = new ProcessStartInfo(host.FileName) { UseShellExecute = false };
                if (host.Assembly is not null) start.ArgumentList.Add(host.Assembly);
            }
        }

        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.WorkingDirectory = sandboxKind == "bubblewrap" ? "/" : Path.GetDirectoryName(artifactPath)!;
        start.Environment.Clear();
        start.Environment["PATH"] = sandboxKind == "bubblewrap"
            ? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        start.Environment["HOME"] = sandboxKind == "bubblewrap"
            ? "/tmp"
            : Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
        start.Environment["TMPDIR"] = sandboxKind == "bubblewrap" ? "/tmp" : Path.GetTempPath();
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["DOTNET_CLI_HOME"] = sandboxKind == "bubblewrap"
            ? "/tmp"
            : Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") ?? Path.GetTempPath();
        if (sandboxKind == "oci")
        {
            // Rootless Podman/Docker may require these host-side control variables; none are forwarded into the container.
            foreach (var variable in new[] { "XDG_RUNTIME_DIR", "DOCKER_HOST", "DOCKER_CONFIG", "CONTAINER_HOST", "CONTAINERS_CONF", "REGISTRY_AUTH_FILE" })
            {
                if (Environment.GetEnvironmentVariable(variable) is { } value) start.Environment[variable] = value;
            }
        }
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { } value) start.Environment[variable] = value;
        }
        return new ParserLaunch(
            start,
            requestArtifactPath,
            sandboxKind is "bubblewrap" or "oci",
            ociEngine,
            ociContainerName);
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (File.Exists(Path.Combine(directory, fileName))) return Path.Combine(directory, fileName);
        }
        return null;
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder(Math.Min(maximum, 16 * 1024));
        var single = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (single[0] == '\n')
            {
                var line = builder.ToString().TrimEnd('\r');
                if (System.Text.Encoding.UTF8.GetByteCount(line) > maximum) throw new InvalidDataException("ParserHost response exceeds budget.");
                return line;
            }
            builder.Append(single[0]);
            if (builder.Length > maximum) throw new InvalidDataException("ParserHost response exceeds budget.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maximum - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(count, remaining));
            truncated |= count > remaining;
        }
        if (truncated) builder.Append("\n[stderr truncated by Morsa]");
        return builder.ToString();
    }

    private static void TryKill(Process process, ParserLaunch launch)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            // The process exited between the checks.
        }
        finally
        {
            TryRemoveOciContainer(launch.OciEngine, launch.OciContainerName);
        }
    }

    private static void TryRemoveOciContainer(string? engine, string? containerName)
    {
        if (engine is null || containerName is null) return;
        try
        {
            var start = new ProcessStartInfo(engine)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("rm");
            start.ArgumentList.Add("--force");
            start.ArgumentList.Add(containerName);
            using var cleanup = Process.Start(start);
            if (cleanup is null) return;
            var output = cleanup.StandardOutput.ReadToEndAsync();
            var error = cleanup.StandardError.ReadToEndAsync();
            if (!cleanup.WaitForExit(5_000)) cleanup.Kill(entireProcessTree: true);
            Task.WaitAll([output, error], 5_000);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            IOException or
            UnauthorizedAccessException or
            AggregateException or
            NotSupportedException)
        {
            // Cleanup is best effort after the primary parser process has already been terminated.
        }
    }

    private sealed record ParserRequest(string Id, ArtifactContext Artifact, ExtractionOptions Options);

    private sealed record ParserLaunch(
        ProcessStartInfo StartInfo,
        string ArtifactPath,
        bool IsSandboxed,
        string? OciEngine,
        string? OciContainerName);
}
