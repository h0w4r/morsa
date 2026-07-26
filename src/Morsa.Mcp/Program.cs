using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Morsa.Application.Abstractions;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync().ConfigureAwait(false);

/// <summary>MCP tools are thin adapters over the same application services as the CLI.</summary>
[McpServerToolType]
public static class MorsaTools
{
    [McpServerTool(Name = "morsa_project_init")]
    [Description("Creates or opens a Morsa workspace and returns its project identifier.")]
    public static async Task<object> ProjectInit(string path, string? name = null, CancellationToken cancellationToken = default)
    {
        await using var provider = BuildProvider(path);
        var initializer = provider.GetRequiredService<IStoreInitializer>();
        var store = provider.GetRequiredService<IMorsaStore>();
        await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var project = await store.Projects.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            project = new MorsaProject { Name = name ?? new DirectoryInfo(path).Name, RootPath = Path.GetFullPath(path) };
            store.Add(project);
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new { schema_version = "1", project_id = project.Id, project.Name, project.RootPath };
    }

    [McpServerTool(Name = "morsa_get_entities")]
    [Description("Returns normalized entities from one Morsa workspace.")]
    public static async Task<object> GetEntities(string path, CancellationToken cancellationToken = default)
    {
        await using var provider = BuildProvider(path);
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        var entities = await provider.GetRequiredService<IMorsaStore>().Entities.ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new { schema_version = "1", entities };
    }

    private static ServiceProvider BuildProvider(string path)
    {
        var services = new ServiceCollection();
        services.AddMorsaCore(Path.GetFullPath(path));
        return services.BuildServiceProvider();
    }
}

