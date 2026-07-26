using Microsoft.Extensions.DependencyInjection;
using Morsa.Cli.Commands;
using Morsa.Cli.Runtime;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Configuration;
using Morsa.Infrastructure.Workspace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Spectre.Console.Cli;

namespace Morsa.Cli;

/// <summary>CLI composition root. Machine-readable output never shares stdout with logs.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var workspacePath = ArgumentPreParser.GetOption(args, "--project") ??
                            WorkspaceContext.Discover().RootPath;
        MorsaConfiguration configuration;
        try
        {
            configuration = MorsaConfigurationLoader.LoadForWorkspace(workspacePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or Tomlyn.TomlException)
        {
            Console.Error.WriteLine($"morsa: invalid configuration: {exception.Message}");
            return 2;
        }
        if (!configuration.Output.Color) Environment.SetEnvironmentVariable("NO_COLOR", "1");
        var logRoot = File.Exists(Path.Combine(workspacePath, "morsa.toml"))
            ? Path.Combine(workspacePath, "logs")
            : GetGlobalStatePath();
        Directory.CreateDirectory(logRoot);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("application", "morsa")
            .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
            .WriteTo.File(
                new JsonFormatter(renderMessage: true),
                Path.Combine(logRoot, "morsa-.jsonl"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 10,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateLogger();
        var services = new ServiceCollection();
        services.AddMorsaCore(workspacePath, configuration);
        var cliOutput = new CliOutput(configuration, args.Contains("--ndjson", StringComparer.Ordinal));
        services.AddSingleton(cliOutput);
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
                ingest.AddCommand<IngestUrlCommand>("url").WithDescription("Inspect and acquire one authorized URL.");
            });
            config.AddBranch("discover", discover =>
            {
                discover.AddCommand<DiscoverDocumentsCommand>("documents").WithDescription("Discover public documents through configured providers.");
                discover.AddCommand<DiscoverHistoryCommand>("history").WithDescription("Discover historical URLs through Common Crawl.");
                discover.AddCommand<DiscoveryImportCommand>("import").WithDescription("Import text, CSV, JSON, NDJSON or HAR results.");
            });
            config.AddBranch("fetch", fetch =>
            {
                fetch.AddCommand<FetchPendingCommand>("pending").WithDescription("Download pending discovered resources.");
                fetch.AddCommand<IngestUrlCommand>("url").WithDescription("Download one authorized URL.");
            });
            config.AddBranch("provider", provider =>
            {
                provider.AddCommand<ProviderListCommand>("list").WithDescription("List discovery providers and health.");
                provider.AddCommand<ProviderListCommand>("status").WithDescription("Check discovery provider health.");
                provider.AddCommand<ProviderBootstrapCommand>("bootstrap").WithDescription("Generate a local provider deployment.");
            });
            config.AddBranch("run", run =>
            {
                run.AddCommand<FullPipelineCommand>("full").WithDescription("Run discovery, acquisition, analysis and correlation.");
                run.AddCommand<ResumePipelineCommand>("resume").WithDescription("Resume pending durable work.");
            });
            config.AddBranch("analyze", analyze =>
                analyze.AddCommand<AnalyzeAllCommand>("all").WithDescription("Analyze every pending artifact."));
            config.AddCommand<CorrelateCommand>("correlate").WithDescription("Build normalized investigation entities.");
            config.AddBranch("recon", recon =>
            {
                recon.AddCommand<ReconDnsCommand>("dns").WithDescription("Query DNS records for an authorized target.");
                recon.AddCommand<ReconReverseCommand>("reverse").WithDescription("Perform reverse DNS lookups.");
                recon.AddCommand<ReconSubdomainsCommand>("subdomains").WithDescription("Enumerate a bounded DNS label dictionary.");
                recon.AddCommand<ReconRangeCommand>("range").WithDescription("Perform bounded PTR enumeration for a CIDR.");
                recon.AddCommand<ReconAxfrCommand>("axfr").WithDescription("Attempt an authorized TCP zone transfer.");
            });
            config.AddBranch("fingerprint", fingerprint =>
            {
                fingerprint.AddCommand<FingerprintHttpCommand>("http").WithDescription("Fingerprint an authorized HTTP service.");
                fingerprint.AddCommand<FingerprintTlsCommand>("tls").WithDescription("Inspect an authorized TLS service.");
                fingerprint.AddCommand<FingerprintBannerCommand>("banner").WithDescription("Collect a bounded service banner.");
            });
            config.AddBranch("web", web =>
            {
                web.AddCommand<WebCrawlCommand>("crawl").WithDescription("Create a bounded same-host HTTP map.");
                web.AddCommand<WebBackupCommand>("backups").WithDescription("Validate evidence-driven backup candidates.");
            });
            config.AddBranch("malware", malware =>
            {
                malware.AddCommand<MalwareScanCommand>("scan").WithDescription("Run local static risk analysis.");
                malware.AddCommand<YaraScanCommand>("yara").WithDescription("Run an installed YARA engine.");
            });
            config.AddBranch("graph", graph =>
                graph.AddCommand<GraphExportCommand>("export").WithDescription("Export GraphML, GEXF, DOT or CSV."));
            config.AddBranch("plugin", plugin =>
            {
                plugin.AddCommand<PluginListCommand>("list").WithDescription("List installed plugin versions.");
                plugin.AddCommand<PluginInspectCommand>("inspect").WithDescription("Inspect installed versions and manifest validation.");
                plugin.AddCommand<PluginInstallCommand>("install").WithDescription("Install a directory or ZIP plugin package.");
                plugin.AddCommand<PluginInstallCommand>("update").WithDescription("Install and activate a newer plugin package.");
                plugin.AddCommand<PluginActivateCommand>("activate").WithDescription("Activate one installed plugin version.");
                plugin.AddCommand<PluginRollbackCommand>("rollback").WithDescription("Activate the previous installed version.");
                plugin.AddCommand<PluginRemoveCommand>("remove").WithDescription("Remove one version or the complete plugin.");
                plugin.AddCommand<PluginRunCommand>("run").WithDescription("Invoke a morsa-plugin/1 operation.");
            });
            config.AddBranch("proxy", proxy =>
            {
                proxy.AddBranch("pool", pool =>
                {
                    pool.AddCommand<ProxyPoolAddCommand>("add").WithDescription("Create or update a proxy pool.");
                    pool.AddCommand<ProxyPoolListCommand>("list").WithDescription("List proxy pools.");
                });
                proxy.AddCommand<ProxyImportCommand>("import").WithDescription("Import user-managed proxy endpoints.");
                proxy.AddBranch("source", source =>
                {
                    source.AddCommand<ProxySourceListCommand>("list").WithDescription("List supported proxy source adapters and environment availability.");
                    source.AddCommand<ProxyImportCommand>("load").WithDescription("Load a proxy source into a named pool.");
                });
                proxy.AddCommand<ProxyStatusCommand>("status").WithDescription("Show endpoint health and counters.");
                proxy.AddCommand<ProxyResetCommand>("reset").WithDescription("Reset health for a pool.");
                proxy.AddCommand<ProxyTestCommand>("test").WithDescription("Test pool connectivity against an HTTPS URL.");
            });
            config.AddBranch("report", report =>
            {
                report.AddCommand<ReportJsonCommand>("json").WithDescription("Export a versioned JSON report.");
                report.AddCommand<ReportHtmlCommand>("html").WithDescription("Export a standalone HTML report.");
                report.AddCommand<ReportCsvCommand>("csv").WithDescription("Export normalized CSV tables.");
                report.AddCommand<ReportBundleCommand>("bundle").WithDescription("Export a reproducible evidence bundle.");
            });
        });

        try
        {
            return await app.RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Operation cancelled by caller");
            return 7;
        }
        catch (Exception exception)
        {
            // Do not serialize exception messages or stack data into persistent logs because URLs may carry sensitive query values.
            Log.Error("Unhandled Morsa CLI failure of type {ExceptionType}", exception.GetType().FullName);
            if (cliOutput.MachineReadable || args.Any(argument => argument is "--json" or "--ndjson"))
            {
                cliOutput.WriteError("morsa.unhandled", exception.Message);
                return exception is Morsa.Application.Models.MorsaException morsaException ? morsaException.ExitCode : 1;
            }
            Console.Error.WriteLine($"morsa: {CliOutput.SanitizeDiagnostic(exception.Message)}");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static string GetGlobalStatePath()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(state)) return Path.Combine(state, "morsa");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Morsa", "logs")
            : Path.Combine(home, ".local", "state", "morsa");
    }
}

internal static class BuildInfo
{
    // Read the SDK-generated informational version so packaged binaries report the requested release version.
    public static string Version { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+', 2)[0]
        ?? "0.0.0-unknown";

    public const string SchemaVersion = "1";
}
