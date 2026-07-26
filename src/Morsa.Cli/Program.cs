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
                ingest.AddCommand<IngestUrlCommand>("url").WithDescription("Inspect and acquire one authorized URL.");
            });
            config.AddBranch("discover", discover =>
            {
                discover.AddCommand<DiscoverDocumentsCommand>("documents").WithDescription("Discover public documents through configured providers.");
                discover.AddCommand<DiscoverHistoryCommand>("history").WithDescription("Discover historical URLs through Common Crawl.");
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
