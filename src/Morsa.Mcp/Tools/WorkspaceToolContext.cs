using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Domain.Runs;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Configuration;

namespace Morsa.Mcp.Tools;

/// <summary>
/// Owns the scoped service provider for one MCP invocation. MCP clients may operate on
/// multiple workspaces, so resolving a fixed workspace at process startup would be unsafe.
/// </summary>
internal sealed class WorkspaceToolContext : IAsyncDisposable
{
    private WorkspaceToolContext(ServiceProvider services, IWorkspaceContext workspace, MorsaProject project)
    {
        Services = services;
        Workspace = workspace;
        Project = project;
    }

    public ServiceProvider Services { get; }

    public IWorkspaceContext Workspace { get; }

    public MorsaProject Project { get; }

    public IMorsaStore Store => Services.GetRequiredService<IMorsaStore>();

    public T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Opens an initialized workspace and requires an existing project record.</summary>
    public static async Task<WorkspaceToolContext> OpenAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var root = WorkspacePathPolicy.NormalizeWorkspace(workspacePath);
        if (!Directory.Exists(root) || (!File.Exists(Path.Combine(root, "morsa.db")) && !File.Exists(Path.Combine(root, "morsa.toml"))))
        {
            throw new DirectoryNotFoundException("The path is not an initialized Morsa workspace.");
        }

        return await OpenCoreAsync(root, null, createProject: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a workspace, initializes persistence and writes its public TOML configuration.</summary>
    public static async Task<WorkspaceToolContext> CreateAsync(
        string workspacePath,
        string? projectName,
        CancellationToken cancellationToken)
    {
        var root = WorkspacePathPolicy.NormalizeWorkspace(workspacePath);
        return await OpenCoreAsync(root, projectName, createProject: true, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    /// <summary>Runs an auditable operation and closes its durable run on success, failure or cancellation.</summary>
    public async Task<(Run Run, T Result)> ExecuteRunAsync<T>(
        string command,
        ActivityMode mode,
        Func<Run, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var coordinator = GetRequiredService<RunCoordinator>();
        return await coordinator.ExecuteAsync(Project.Id, command, mode, operation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkspaceToolContext> OpenCoreAsync(
        string root,
        string? projectName,
        bool createProject,
        CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMorsaCore(root);
        var provider = services.BuildServiceProvider();
        try
        {
            var initializer = provider.GetRequiredService<IStoreInitializer>();
            var store = provider.GetRequiredService<IMorsaStore>();
            var workspace = provider.GetRequiredService<IWorkspaceContext>();
            await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var project = await store.Projects.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (project is null && createProject)
            {
                project = new MorsaProject
                {
                    Name = NormalizeProjectName(projectName, root),
                    RootPath = root,
                };
                store.Add(project);
                await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (project is null)
            {
                throw new InvalidOperationException("The workspace database does not contain a Morsa project.");
            }

            if (!WorkspacePathPolicy.PathEquals(project.RootPath, root))
            {
                throw new InvalidOperationException("The persisted project root does not match the requested workspace path.");
            }

            if (createProject && !File.Exists(workspace.ConfigurationPath))
            {
                await MorsaConfigurationLoader.SaveAsync(
                    workspace.ConfigurationPath,
                    new MorsaConfiguration { Project = new ProjectConfiguration { Name = project.Name } },
                    cancellationToken).ConfigureAwait(false);
            }

            return new WorkspaceToolContext(provider, workspace, project);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string NormalizeProjectName(string? requestedName, string root)
    {
        var value = string.IsNullOrWhiteSpace(requestedName) ? new DirectoryInfo(root).Name : requestedName.Trim();
        if (value.Length is < 1 or > 120 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Project name must contain 1 to 120 printable characters.", nameof(requestedName));
        }

        return value;
    }

}

/// <summary>Centralizes workspace confinement and scope normalization for every MCP tool.</summary>
internal static class WorkspacePathPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizeWorkspace(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Workspace path is invalid.", nameof(path));
        }

        RejectExistingLink(fullPath);
        return fullPath;
    }

    /// <summary>Resolves an input file and rejects access outside the selected workspace.</summary>
    public static string ResolveInputFile(IWorkspaceContext workspace, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(workspace.RootPath, path));
        EnsureContained(workspace.RootPath, candidate, "Input file must be inside the Morsa workspace.");
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException("Input file does not exist.", candidate);
        }

        RejectLinksBetween(workspace.RootPath, candidate);
        return candidate;
    }

    /// <summary>Resolves report output below reports/ and prevents arbitrary filesystem writes.</summary>
    public static string ResolveReportOutput(IWorkspaceContext workspace, string? path, string defaultName)
    {
        var reportsRoot = Path.GetFullPath(workspace.ReportsPath);
        var candidate = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(reportsRoot, defaultName)
            : Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(reportsRoot, path));
        EnsureContained(reportsRoot, candidate, "Report output must be inside the workspace reports directory.");
        RejectLinksBetween(workspace.RootPath, candidate);
        return candidate;
    }

    public static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    public static string NormalizeScopeValue(string value, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        return kind switch
        {
            "url" when Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" =>
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
            "domain" or "host" => new IdnMapping().GetAscii(trimmed.TrimEnd('.')).ToLowerInvariant(),
            "ip" when System.Net.IPAddress.TryParse(trimmed, out var address) => address.ToString(),
            "cidr" => NormalizeCidr(trimmed),
            "url" => throw new ArgumentException("URL scope must be an absolute HTTP or HTTPS URI.", nameof(value)),
            "ip" => throw new ArgumentException("IP scope must be a valid IPv4 or IPv6 address.", nameof(value)),
            _ => throw new ArgumentException("Scope kind must be domain, host, url, ip or cidr.", nameof(kind)),
        };
    }

    public static string InferScopeKind(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return "url";
        if (System.Net.IPAddress.TryParse(value, out _)) return "ip";
        if (value.Contains('/') && System.Net.IPAddress.TryParse(value.Split('/', 2)[0], out _)) return "cidr";
        return value.Contains('.') ? "domain" : "host";
    }

    private static string NormalizeCidr(string value)
    {
        var parts = value.Split('/', 2);
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
            prefix < 0 || prefix > address.GetAddressBytes().Length * 8)
        {
            throw new ArgumentException("CIDR scope must be a valid IPv4 or IPv6 prefix.", nameof(value));
        }

        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        }

        return $"{new System.Net.IPAddress(bytes)}/{prefix}";
    }

    private static void EnsureContained(string root, string candidate, string message)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) || Path.IsPathFullyQualified(relative))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    private static void RejectLinksBetween(string root, string candidate)
    {
        RejectExistingLink(root);
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                RejectExistingLink(current);
            }
        }
    }

    private static void RejectExistingLink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Symbolic links and reparse points are not accepted at MCP workspace boundaries.");
        }
    }
}

internal static class McpContract
{
    public const string SchemaVersion = "1";
}
