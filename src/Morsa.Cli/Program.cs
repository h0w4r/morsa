using Microsoft.Extensions.DependencyInjection;
using Morsa.Cli.Commands;
using Morsa.Cli.Runtime;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Workspace;
using Spectre.Console.Cli;

namespace Morsa.Cli;

/// <summary>CLI composition root. Machine-readable output never shares stdout with logs.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var workspacePath = ArgumentPreParser.GetOption(args, "--project") ??
                            WorkspaceContext.Discover().RootPath;
        var services = new ServiceCollection();
        services.AddMorsaCore(workspacePath);
        services.AddSingleton<CliOutput>();
        services.AddMorsaCommands();

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("morsa");
            config.SetApplicationVersion(BuildInfo.Version);
            config.Settings.ShowOptionDefaultValues = true;
            config.Settings.PropagateExceptions = false;

            config.AddCommand<InitCommand>("init").WithDescription("Create a self-contained Morsa workspace.");
            config.AddCommand<DoctorCommand>("doctor").WithDescription("Inspect runtime, sandbox and workspace health.");
            config.AddCommand<VersionCommand>("version").WithDescription("Print version and contract information.");

            config.AddBranch("project", project =>
                project.AddCommand<ProjectStatusCommand>("status").WithDescription("Show project and run status."));
            config.AddBranch("scope", scope =>
            {
                scope.AddCommand<ScopeAddCommand>("add").WithDescription("Add an authorized scope entry.");
                scope.AddCommand<ScopeListCommand>("list").WithDescription("List authorized targets.");
            });
            config.AddBranch("ingest", ingest =>
            {
                ingest.AddCommand<IngestFileCommand>("file").WithDescription("Ingest one local artifact.");
                ingest.AddCommand<IngestDirectoryCommand>("directory").WithDescription("Ingest files from a directory.");
            });
            config.AddBranch("analyze", analyze =>
                analyze.AddCommand<AnalyzeAllCommand>("all").WithDescription("Analyze every pending artifact."));
            config.AddCommand<CorrelateCommand>("correlate").WithDescription("Build normalized investigation entities.");
            config.AddBranch("proxy", proxy =>
            {
                proxy.AddBranch("pool", pool =>
                {
                    pool.AddCommand<ProxyPoolAddCommand>("add").WithDescription("Create or update a proxy pool.");
                    pool.AddCommand<ProxyPoolListCommand>("list").WithDescription("List proxy pools.");
                });
                proxy.AddCommand<ProxyImportCommand>("import").WithDescription("Import user-managed proxy endpoints.");
                proxy.AddCommand<ProxyStatusCommand>("status").WithDescription("Show endpoint health and counters.");
                proxy.AddCommand<ProxyResetCommand>("reset").WithDescription("Reset health for a pool.");
                proxy.AddCommand<ProxyTestCommand>("test").WithDescription("Test pool connectivity against an HTTPS URL.");
            });
            config.AddBranch("report", report =>
            {
                report.AddCommand<ReportJsonCommand>("json").WithDescription("Export a versioned JSON report.");
                report.AddCommand<ReportHtmlCommand>("html").WithDescription("Export a standalone HTML report.");
            });
        });

        try
        {
            return await app.RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 7;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"morsa: {exception.Message}");
            return 1;
        }
    }
}

internal static class BuildInfo
{
    public const string Version = "0.1.0-alpha.1";
    public const string SchemaVersion = "1";
}

