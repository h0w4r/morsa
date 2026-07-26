using Morsa.Application.Abstractions;

namespace Morsa.Infrastructure.Workspace;

/// <summary>Canonical paths for one Morsa workspace.</summary>
public sealed class WorkspaceContext : IWorkspaceContext
{
    public WorkspaceContext(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        DatabasePath = Path.Combine(RootPath, "morsa.db");
        ConfigurationPath = Path.Combine(RootPath, "morsa.toml");
        ArtifactsPath = Path.Combine(RootPath, "artifacts");
        ReportsPath = Path.Combine(RootPath, "reports");
        LogsPath = Path.Combine(RootPath, "logs");
    }

    public string RootPath { get; }

    public string DatabasePath { get; }

    public string ConfigurationPath { get; }

    public string ArtifactsPath { get; }

    public string ReportsPath { get; }

    public string LogsPath { get; }

    /// <summary>Finds the closest parent containing morsa.toml.</summary>
    public static WorkspaceContext Discover(string? startPath = null)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath ?? Environment.CurrentDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "morsa.toml")))
            {
                return new WorkspaceContext(current.FullName);
            }

            current = current.Parent;
        }

        return new WorkspaceContext(startPath ?? Environment.CurrentDirectory);
    }

    /// <summary>Creates the fixed directory layout with no symlink traversal.</summary>
    public void EnsureDirectories()
    {
        RejectSymlinkBoundary(RootPath);
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ArtifactsPath);
        Directory.CreateDirectory(Path.Combine(ArtifactsPath, "raw"));
        Directory.CreateDirectory(Path.Combine(ArtifactsPath, "by-hash"));
        Directory.CreateDirectory(Path.Combine(ArtifactsPath, "quarantine"));
        Directory.CreateDirectory(Path.Combine(RootPath, "cache"));
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(ReportsPath);
        if (!OperatingSystem.IsWindows())
        {
            var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            foreach (var directory in new[] { RootPath, ArtifactsPath, Path.Combine(RootPath, "cache"), LogsPath, ReportsPath })
                File.SetUnixFileMode(directory, privateMode);
        }
    }

    private static void RejectSymlinkBoundary(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (!current.Exists) continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException($"Workspace path crosses a symbolic link or reparse point: {current.FullName}");
        }
    }
}
