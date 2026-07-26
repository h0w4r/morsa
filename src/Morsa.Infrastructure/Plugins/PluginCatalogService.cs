using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Recon;

namespace Morsa.Infrastructure.Plugins;

/// <summary>On-disk manifest for a versioned external or managed plugin package.</summary>
public sealed record InstalledPluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string ApiVersion,
    string Kind,
    string EntryPoint,
    IReadOnlyList<string>? Arguments,
    IReadOnlyList<string>? Permissions,
    IReadOnlyList<string>? SecretEnvironmentVariables,
    string? Sha256,
    string? Description);

public sealed record InstalledPlugin(
    InstalledPluginManifest Manifest,
    string Directory,
    bool IsCurrent,
    bool IsValid,
    string? ValidationError);

public sealed record PluginProcessResult(
    string PluginId,
    string Version,
    int ExitCode,
    JsonElement? Response,
    string StandardError,
    TimeSpan Duration);

/// <summary>Maintains plugin versions without allowing package path traversal.</summary>
public sealed class PluginCatalogService(IWorkspaceContext workspace)
{
    private const string ManifestName = "morsa-plugin.json";
    private const int MaximumPackageEntries = 10_000;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const long MaximumPackageFileBytes = 256L * 1024 * 1024;
    private const long MaximumManifestBytes = 1024L * 1024;

    public string RootPath => Path.Combine(workspace.RootPath, ".morsa", "plugins");

    public async Task<InstalledPlugin> InstallAsync(string source, bool activate, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(RootPath, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            if (Directory.Exists(source))
            {
                var sourceDirectory = Path.GetFullPath(source);
                RejectReparsePoint(sourceDirectory, "Plugin source directory");
                CopyDirectory(sourceDirectory, staging);
            }
            else if (File.Exists(source) && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractZipSafely(Path.GetFullPath(source), staging);
            }
            else
            {
                throw new FileNotFoundException("Plugin source must be a directory or ZIP package.", source);
            }

            var manifest = await LoadManifestAsync(staging, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest);
            var entry = ResolveContainedPath(staging, manifest.EntryPoint);
            if (!File.Exists(entry)) throw new InvalidDataException($"Plugin entry point '{manifest.EntryPoint}' does not exist.");
            if (!string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                var actual = await ComputeSha256Async(entry, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(manifest.Sha256)))
                {
                    throw new InvalidDataException("Plugin entry point checksum does not match its manifest.");
                }
            }

            var destination = ResolvePluginVersion(manifest.Id, manifest.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                RejectReparsePoint(destination, "Existing plugin destination");
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(staging, destination);
            if (activate) await ActivateAsync(manifest.Id, manifest.Version, cancellationToken).ConfigureAwait(false);
            return new InstalledPlugin(manifest, destination, activate, true, null);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<IReadOnlyList<InstalledPlugin>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RootPath)) return [];
        var plugins = new List<InstalledPlugin>();
        foreach (var idDirectory in Directory.EnumerateDirectories(RootPath).Where(path => Path.GetFileName(path) != ".staging"))
        {
            var current = await ReadCurrentAsync(Path.GetFileName(idDirectory), cancellationToken).ConfigureAwait(false);
            foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory))
            {
                try
                {
                    var manifest = await LoadManifestAsync(versionDirectory, cancellationToken).ConfigureAwait(false);
                    ValidateManifest(manifest);
                    plugins.Add(new InstalledPlugin(manifest, versionDirectory, manifest.Version == current, true, null));
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    var fallback = new InstalledPluginManifest(Path.GetFileName(idDirectory), Path.GetFileName(idDirectory), Path.GetFileName(versionDirectory), "unknown", "unknown", "unknown", string.Empty, [], [], [], null, null);
                    plugins.Add(new InstalledPlugin(fallback, versionDirectory, false, false, exception.Message));
                }
            }
        }

        return plugins.OrderBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal).ThenByDescending(plugin => plugin.Manifest.Version, StringComparer.Ordinal).ToArray();
    }

    public async Task ActivateAsync(string id, string version, CancellationToken cancellationToken)
    {
        var directory = ResolvePluginVersion(id, version);
        var manifest = await LoadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest);
        if (!manifest.Id.Equals(id, StringComparison.Ordinal) || !manifest.Version.Equals(version, StringComparison.Ordinal))
            throw new InvalidDataException("Installed plugin manifest does not match its catalog path.");
        var pointer = Path.Combine(RootPath, id, "current.txt");
        await File.WriteAllTextAsync(pointer + ".tmp", version + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        File.Move(pointer + ".tmp", pointer, overwrite: true);
    }

    public async Task<string> RollbackAsync(string id, CancellationToken cancellationToken)
    {
        var versions = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(plugin => plugin.Manifest.Id == id && plugin.IsValid)
            .OrderByDescending(plugin => plugin.Manifest.Version, StringComparer.Ordinal)
            .ToArray();
        var current = versions.SingleOrDefault(plugin => plugin.IsCurrent);
        var previous = versions.FirstOrDefault(plugin => !plugin.IsCurrent &&
            (current is null || string.CompareOrdinal(plugin.Manifest.Version, current.Manifest.Version) < 0));
        if (previous is null) throw new InvalidOperationException($"Plugin '{id}' has no previous installed version.");
        await ActivateAsync(id, previous.Manifest.Version, cancellationToken).ConfigureAwait(false);
        return previous.Manifest.Version;
    }

    public Task RemoveAsync(string id, string? version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var idRoot = ResolveContainedPath(RootPath, id);
        if (version is null)
        {
            if (Directory.Exists(idRoot)) Directory.Delete(idRoot, recursive: true);
            return Task.CompletedTask;
        }

        var directory = ResolvePluginVersion(id, version);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        var pointer = Path.Combine(idRoot, "current.txt");
        if (File.Exists(pointer) && string.Equals(File.ReadAllText(pointer).Trim(), version, StringComparison.Ordinal)) File.Delete(pointer);
        return Task.CompletedTask;
    }

    public async Task<InstalledPlugin> GetCurrentAsync(string id, CancellationToken cancellationToken)
    {
        ValidatePluginId(id);
        var version = await ReadCurrentAsync(id, cancellationToken).ConfigureAwait(false) ??
                      throw new InvalidOperationException($"Plugin '{id}' has no active version.");
        var directory = ResolvePluginVersion(id, version);
        var manifest = await LoadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        ValidateManifest(manifest);
        if (!manifest.Id.Equals(id, StringComparison.Ordinal) || !manifest.Version.Equals(version, StringComparison.Ordinal))
            throw new InvalidDataException("Installed plugin manifest does not match its catalog path.");
        var entry = ResolveContainedPath(directory, manifest.EntryPoint);
        if (!File.Exists(entry)) throw new InvalidDataException("Installed plugin entry point does not exist.");
        RejectReparsePoint(entry, "Installed plugin entry point");
        return new InstalledPlugin(manifest, directory, true, true, null);
    }

    private async Task<string?> ReadCurrentAsync(string id, CancellationToken cancellationToken)
    {
        var pointer = Path.Combine(RootPath, id, "current.txt");
        return File.Exists(pointer) ? (await File.ReadAllTextAsync(pointer, cancellationToken).ConfigureAwait(false)).Trim() : null;
    }

    private string ResolvePluginVersion(string id, string version) => ResolveContainedPath(RootPath, Path.Combine(id, version));

    private static async Task<InstalledPluginManifest> LoadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) throw new InvalidDataException($"Package does not contain {ManifestName}.");
        RejectReparsePoint(path, "Plugin manifest");
        if (new FileInfo(path).Length > MaximumManifestBytes) throw new InvalidDataException("Plugin manifest exceeds 1 MiB.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<InstalledPluginManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
               throw new InvalidDataException("Plugin manifest is empty.");
    }

    private static void ValidateManifest(InstalledPluginManifest manifest)
    {
        ValidatePluginId(manifest.Id);
        if (string.IsNullOrWhiteSpace(manifest.Version) ||
            !System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$") ||
            manifest.Version is "." or "..") throw new InvalidDataException("Plugin version is invalid.");
        if (manifest.ApiVersion != "1") throw new InvalidDataException($"Unsupported plugin API '{manifest.ApiVersion}'.");
        if (manifest.Kind is not ("process" or "dotnet-process" or "managed")) throw new InvalidDataException($"Unsupported plugin kind '{manifest.Kind}'.");
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint) || Path.IsPathRooted(manifest.EntryPoint)) throw new InvalidDataException("Plugin entry point must be package-relative.");
        if (manifest.Kind == "managed" && !Path.GetExtension(manifest.EntryPoint).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Managed plugin entry point must be a .NET assembly.");
        if ((manifest.Arguments ?? []).Count > 128 || (manifest.Arguments ?? []).Any(argument => argument.Length > 8 * 1024))
            throw new InvalidDataException("Plugin arguments exceed the configured count or length budget.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "network", "filesystem:read", "filesystem:write", "secrets", "process" };
        if ((manifest.Permissions ?? []).Any(permission => !allowed.Contains(permission))) throw new InvalidDataException("Plugin requests an unknown permission.");
        if ((manifest.SecretEnvironmentVariables ?? []).Any(name => !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][A-Z0-9_]{1,127}$")))
            throw new InvalidDataException("Plugin secret environment variable name is invalid.");
        if ((manifest.SecretEnvironmentVariables?.Count ?? 0) > 0 && !(manifest.Permissions ?? []).Contains("secrets", StringComparer.Ordinal))
            throw new InvalidDataException("Plugin declares secret variables without the secrets permission.");
        if (manifest.Sha256 is { Length: > 0 } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) throw new InvalidDataException("Plugin SHA-256 is invalid.");
    }

    private static void ValidatePluginId(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{1,63}$"))
            throw new InvalidDataException("Plugin id is invalid.");
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(fullRoot, comparison)) throw new InvalidDataException("Plugin path escapes its package root.");
        return full;
    }

    private static void ExtractZipSafely(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaximumPackageEntries) throw new InvalidDataException("Plugin package exceeds the entry budget.");
        long totalLength = 0;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (IsSymbolicLink(entry)) throw new InvalidDataException($"Plugin package contains a symbolic link: {entry.FullName}");
            totalLength = checked(totalLength + entry.Length);
            if (entry.Length > MaximumPackageFileBytes || totalLength > MaximumPackageBytes)
                throw new InvalidDataException("Plugin package exceeds the uncompressed byte budget.");
            if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 1_000)
                throw new InvalidDataException($"Plugin package entry has a suspicious compression ratio: {entry.FullName}");
            var target = ResolveContainedPath(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        var entries = 0;
        long totalLength = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            if (++entries > MaximumPackageEntries) throw new InvalidDataException("Plugin directory exceeds the entry budget.");
            Directory.CreateDirectory(ResolveContainedPath(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            if (++entries > MaximumPackageEntries) throw new InvalidDataException("Plugin directory exceeds the entry budget.");
            RejectReparsePoint(file, "Plugin package file");
            var length = new FileInfo(file).Length;
            totalLength = checked(totalLength + length);
            if (length > MaximumPackageFileBytes || totalLength > MaximumPackageBytes)
                throw new InvalidDataException("Plugin directory exceeds the byte budget.");
            var target = ResolveContainedPath(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        // ZIP stores Unix file type bits in the high half of ExternalAttributes.
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & unixFileTypeMask) == unixSymbolicLink;
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{description} must not be a symbolic link or reparse point.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
}

/// <summary>Executes one active plugin through the morsa-plugin/1 JSONL contract.</summary>
public sealed class PluginProcessRunner(PluginCatalogService catalog, IMorsaStore store, IWorkspaceContext workspace)
{
    public async Task<PluginProcessResult> RunAsync(
        string pluginId,
        string operation,
        JsonElement input,
        Guid? runId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var plugin = await catalog.GetCurrentAsync(pluginId, cancellationToken).ConfigureAwait(false);
        var entry = Path.GetFullPath(Path.Combine(plugin.Directory, plugin.Manifest.EntryPoint));
        var execution = new PluginExecution { RunId = runId, PluginId = pluginId, PluginVersion = plugin.Manifest.Version };
        store.Add(execution);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            var start = BuildStartInfo(plugin, entry);
            process = Process.Start(start) ?? throw new InvalidOperationException("Plugin process could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            // Drain stderr from process start so a noisy plugin cannot fill the pipe and deadlock stdout.
            var stderrTask = ReadBoundedAsync(process.StandardError, 1024 * 1024, deadline.Token);
            var initialize = JsonSerializer.Serialize(new { type = "initialize", protocol = "morsa-plugin/1", plugin_id = pluginId, permissions = plugin.Manifest.Permissions ?? [] });
            await process.StandardInput.WriteLineAsync(initialize.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "request", id = Guid.NewGuid().ToString("N"), operation, input }).AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            // Plugins may emit an initialized acknowledgement before the operation result.
            string? responseLine = null;
            for (var messageIndex = 0; messageIndex < 16; messageIndex++)
            {
                var candidate = await ReadBoundedLineAsync(process.StandardOutput, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
                if (candidate is null) break;
                using var candidateDocument = JsonDocument.Parse(candidate);
                var type = candidateDocument.RootElement.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
                if (type is "result" or "error")
                {
                    responseLine = candidate;
                    break;
                }
            }
            // Continue draining stdout after the first result so post-result noise cannot fill the pipe.
            var stdoutDrainTask = DrainAsync(process.StandardOutput, deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stdoutDrainTask.ConfigureAwait(false);
            var stderr = RedactSecrets(await stderrTask.ConfigureAwait(false), plugin.Manifest.SecretEnvironmentVariables);
            if (responseLine is null) throw new InvalidDataException("Plugin exited without a result or error message.");
            responseLine = responseLine is null ? null : RedactSecrets(responseLine, plugin.Manifest.SecretEnvironmentVariables);
            JsonElement? response = responseLine is null ? null : JsonDocument.Parse(responseLine).RootElement.Clone();
            var responseIsError = response is { } responseValue &&
                responseValue.TryGetProperty("type", out var responseType) && responseType.GetString() == "error";
            // A protocol-level error is a failed execution even when the adapter exits cleanly.
            var effectiveExitCode = process.ExitCode != 0 ? process.ExitCode : responseIsError ? 8 : 0;
            execution.Status = effectiveExitCode == 0 ? "completed" : "failed";
            execution.ExitCode = effectiveExitCode;
            if (responseIsError && response is { } errorResponse && errorResponse.TryGetProperty("code", out var errorCode))
                execution.ErrorCode = errorCode.GetString();
            execution.FinishedAt = DateTimeOffset.UtcNow;
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new PluginProcessResult(pluginId, plugin.Manifest.Version, effectiveExitCode, response, stderr, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            execution.Status = "timed_out";
            execution.ErrorCode = "PLUGIN_TIMEOUT";
            execution.FinishedAt = DateTimeOffset.UtcNow;
            await store.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException($"Plugin '{pluginId}' exceeded {timeout}.");
        }
        catch (Exception exception)
        {
            TryKill(process);
            execution.Status = "failed";
            execution.ErrorCode = exception.GetType().Name;
            execution.FinishedAt = DateTimeOffset.UtcNow;
            await store.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private ProcessStartInfo BuildStartInfo(InstalledPlugin plugin, string entry)
    {
        var host = plugin.Manifest.Kind == "managed" ? ResolvePluginHostPath() : null;
        var start = plugin.Manifest.Kind switch
        {
            "dotnet-process" => new ProcessStartInfo("dotnet"),
            "managed" when string.Equals(Path.GetExtension(host), ".dll", StringComparison.OrdinalIgnoreCase) => new ProcessStartInfo("dotnet"),
            "managed" => new ProcessStartInfo(host!),
            _ => new ProcessStartInfo(entry),
        };
        if (plugin.Manifest.Kind == "dotnet-process") start.ArgumentList.Add(entry);
        if (plugin.Manifest.Kind == "managed")
        {
            if (string.Equals(Path.GetExtension(host), ".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host!);
            start.ArgumentList.Add("--managed");
            start.ArgumentList.Add(entry);
        }
        foreach (var argument in plugin.Manifest.Arguments ?? []) start.ArgumentList.Add(argument);
        start.WorkingDirectory = plugin.Directory;
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["MORSA_PLUGIN_PROTOCOL"] = "morsa-plugin/1";
        start.Environment["MORSA_WORKSPACE"] = workspace.RootPath;
        start.Environment["MORSA_PLUGIN_PERMISSIONS"] = string.Join(',', plugin.Manifest.Permissions ?? []);
        foreach (var name in plugin.Manifest.SecretEnvironmentVariables ?? [])
        {
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        }

        return OperatingSystem.IsLinux() && FindOnPath("bwrap") is { } bwrap
            ? BuildLinuxSandbox(start, plugin, entry, host, bwrap)
            : start;
    }

    private ProcessStartInfo BuildLinuxSandbox(ProcessStartInfo inner, InstalledPlugin plugin, string entry, string? host, string bwrap)
    {
        var limiter = FindOnPath("prlimit");
        var sandbox = new ProcessStartInfo(limiter ?? bwrap)
        {
            WorkingDirectory = plugin.Directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // ProcessStartInfo otherwise inherits the complete parent environment, including unrelated secrets.
        sandbox.Environment.Clear();
        if (limiter is not null)
        {
            foreach (var argument in new[] { "--as=536870912", "--cpu=300", "--nproc=64", "--nofile=256", "--", bwrap }) sandbox.ArgumentList.Add(argument);
        }

        foreach (var argument in new[] { "--die-with-parent", "--new-session", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--cap-drop", "ALL", "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--dir", "/plugin", "--ro-bind", plugin.Directory, "/plugin" })
            sandbox.ArgumentList.Add(argument);
        foreach (var systemPath in new[] { "/usr", "/bin", "/lib", "/lib64", "/etc/ssl/certs" }.Where(Directory.Exists))
        {
            sandbox.ArgumentList.Add("--ro-bind");
            sandbox.ArgumentList.Add(systemPath);
            sandbox.ArgumentList.Add(systemPath);
        }
        if (plugin.Manifest.Kind == "managed")
        {
            // The host and its stable SDK dependencies are read-only inside the sandbox.
            var hostDirectory = Path.GetDirectoryName(host!)!;
            sandbox.ArgumentList.Add("--ro-bind");
            sandbox.ArgumentList.Add(hostDirectory);
            sandbox.ArgumentList.Add("/morsa-host");
        }
        if (!(plugin.Manifest.Permissions ?? []).Contains("network", StringComparer.Ordinal)) sandbox.ArgumentList.Add("--unshare-net");
        if ((plugin.Manifest.Permissions ?? []).Contains("filesystem:write", StringComparer.Ordinal))
        {
            sandbox.ArgumentList.Add("--bind"); sandbox.ArgumentList.Add(workspace.RootPath); sandbox.ArgumentList.Add("/workspace");
        }
        else if ((plugin.Manifest.Permissions ?? []).Contains("filesystem:read", StringComparer.Ordinal))
        {
            sandbox.ArgumentList.Add("--ro-bind"); sandbox.ArgumentList.Add(workspace.RootPath); sandbox.ArgumentList.Add("/workspace");
        }
        else
        {
            sandbox.ArgumentList.Add("--dir"); sandbox.ArgumentList.Add("/workspace");
        }
        sandbox.ArgumentList.Add("--chdir"); sandbox.ArgumentList.Add("/plugin");
        var sandboxEntry = $"/plugin/{Path.GetRelativePath(plugin.Directory, entry).Replace('\\', '/')}";
        if (plugin.Manifest.Kind == "managed")
        {
            sandbox.ArgumentList.Add(string.Equals(Path.GetExtension(host), ".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : $"/morsa-host/{Path.GetFileName(host)}");
            if (string.Equals(Path.GetExtension(host), ".dll", StringComparison.OrdinalIgnoreCase))
                sandbox.ArgumentList.Add($"/morsa-host/{Path.GetFileName(host)}");
            sandbox.ArgumentList.Add("--managed");
            sandbox.ArgumentList.Add(sandboxEntry);
        }
        else
        {
            sandbox.ArgumentList.Add(plugin.Manifest.Kind == "dotnet-process" ? "dotnet" : sandboxEntry);
            if (plugin.Manifest.Kind == "dotnet-process") sandbox.ArgumentList.Add(sandboxEntry);
        }
        foreach (var argument in plugin.Manifest.Arguments ?? []) sandbox.ArgumentList.Add(argument);

        foreach (var variable in inner.Environment) sandbox.Environment[variable.Key] = variable.Value;
        return sandbox;
    }

    /// <summary>Locates the separately published managed-plugin host without loading plugin code.</summary>
    private static string ResolvePluginHostPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MORSA_PLUGIN_HOST"),
            Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "morsa-plugin-host.exe" : "morsa-plugin-host"),
            Path.Combine(AppContext.BaseDirectory, "morsa-plugin-host.dll"),
        };
        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath)) return fullPath;
        }

        throw new FileNotFoundException(
            "Managed plugin host was not found. Install morsa-plugin-host beside Morsa or set MORSA_PLUGIN_HOST.");
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, executable);
            if (File.Exists(path)) return path;
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
                if (System.Text.Encoding.UTF8.GetByteCount(line) > maximum)
                    throw new InvalidDataException("Plugin response exceeds the maximum size.");
                return line;
            }
            builder.Append(single[0]);
            // UTF-8 never uses fewer than one byte per UTF-16 code unit, so this is a safe early cap.
            if (builder.Length > maximum)
                throw new InvalidDataException("Plugin response exceeds the maximum size.");
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

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            // Deliberately discard output after the bounded protocol result.
        }
    }

    private static string RedactSecrets(string value, IReadOnlyList<string>? secretNames)
    {
        foreach (var name in secretNames ?? [])
        {
            var secret = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }
        return value;
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }
}
