using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Cli.Runtime;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Workspace;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Morsa.Cli.Commands;

public sealed class InitSettings : WorkspaceSettings
{
    [CommandArgument(0, "[PATH]")]
    public string? Path { get; init; }

    [CommandOption("--name <NAME>")]
    public string? Name { get; init; }
}

/// <summary>Creates an independent project and its database.</summary>
public sealed class InitCommand(CliOutput output) : AsyncCommand<InitSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(settings.Path ?? settings.ProjectPath ?? Environment.CurrentDirectory);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddMorsaCore(root);
        await using var provider = services.BuildServiceProvider();
        var initializer = provider.GetRequiredService<IStoreInitializer>();
        var store = provider.GetRequiredService<IMorsaStore>();
        var workspace = provider.GetRequiredService<IWorkspaceContext>();
        await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var project = await store.Projects.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            project = new MorsaProject
            {
                Name = settings.Name ?? new DirectoryInfo(root).Name,
                RootPath = root,
            };
            store.Add(project);
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(workspace.ConfigurationPath))
        {
            await MorsaConfigurationLoader.SaveAsync(
                workspace.ConfigurationPath,
                new MorsaConfiguration { Project = new ProjectConfiguration { Name = project.Name } },
                cancellationToken).ConfigureAwait(false);
        }

        output.Write(new { project.Id, project.Name, project.RootPath }, settings.Json);
        return 0;
    }
}

/// <summary>Prints the stable application and schema versions.</summary>
public sealed class VersionCommand(CliOutput output) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        output.Write(new { version = BuildInfo.Version, schema_version = BuildInfo.SchemaVersion }, settings.Json);
        return 0;
    }
}

/// <summary>Reports real environment capabilities instead of inferred readiness.</summary>
public sealed class DoctorCommand(
    IStoreInitializer initializer,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var database = true;
        string? databaseError = null;
        try
        {
            await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            database = false;
            databaseError = exception.Message;
        }

        var bwrap = FindOnPath("bwrap");
        var podman = FindOnPath("podman");
        var docker = FindOnPath("docker");
        var report = new
        {
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            workspace = workspace.RootPath,
            database,
            database_error = databaseError,
            sandbox = bwrap is not null ? "bubblewrap" : podman is not null || docker is not null ? "oci" : "process-restricted",
            tools = new { bubblewrap = bwrap, podman, docker, yara = FindOnPath("yara"), clamav = FindOnPath("clamscan") },
        };
        output.Write(report, settings.Json);
        return database ? 0 : 10;
    }

    private static string? FindOnPath(string executable)
    {
        var names = OperatingSystem.IsWindows() ? new[] { executable + ".exe", executable + ".cmd", executable } : [executable];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>Displays durable project counters and the most recent runs.</summary>
public sealed class ProjectStatusCommand(
    IStoreInitializer initializer,
    IMorsaStore store,
    IWorkspaceContext workspace,
    CliOutput output) : AsyncCommand<WorkspaceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken)
    {
        var project = await CommandHelpers.RequireProjectAsync(initializer, store, workspace, cancellationToken).ConfigureAwait(false);
        var report = new
        {
            project.Id,
            project.Name,
            project.RootPath,
            scopes = store.ScopeEntries.Count(),
            runs = store.Runs.Count(),
            artifacts = store.Artifacts.Count(),
            observations = store.MetadataObservations.Count(),
            entities = store.Entities.Count(),
            findings = store.Findings.Count(),
            recent_runs = store.Runs.OrderByDescending(run => run.CreatedAt).Take(10).ToArray(),
        };
        output.Write(report, settings.Json);
        return 0;
    }
}

internal static class CommandRegistration
{
    public static IServiceCollection AddMorsaCommands(this IServiceCollection services)
    {
        var commands = typeof(CommandRegistration).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.Name.EndsWith("Command", StringComparison.Ordinal));
        foreach (var command in commands)
        {
            services.AddTransient(command);
        }

        return services;
    }
}

