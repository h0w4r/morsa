using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Correlation;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Reporting;

namespace Morsa.IntegrationTests;

public sealed class GraphExporterIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-graph", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("graphml")]
    [InlineData("gexf")]
    public async Task ExportAsync_XmlFormats_ProducesWellFormedEscapedDocument(string format)
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var projectId = Guid.NewGuid();
        var source = new EntityNode { ProjectId = projectId, Type = "email", Value = "a&b@example.test", NormalizedValue = "a&b@example.test", Confidence = 1 };
        var target = new EntityNode { ProjectId = projectId, Type = "application", Value = "Office <Suite>", NormalizedValue = "office <suite>", Confidence = 0.9 };
        var store = provider.GetRequiredService<IMorsaStore>();
        store.AddRange([source, target]);
        store.Add(new EntityRelation { ProjectId = projectId, FromEntityId = source.Id, ToEntityId = target.Id, Type = "observed & related", EvidenceId = Guid.NewGuid(), Confidence = 0.8 });
        await store.SaveChangesAsync();
        var path = Path.Combine(_root, "reports", $"graph.{format}");

        await provider.GetRequiredService<GraphExporter>().ExportAsync(projectId, format, path, CancellationToken.None);

        var document = XDocument.Load(path);
        Assert.NotNull(document.Root);
        var decodedValues = document.Descendants().Select(item => item.Value)
            .Concat(document.Descendants().Attributes().Select(item => item.Value));
        Assert.Contains(decodedValues, value => value.Contains("a&b@example.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportAsync_DotAndCsv_EscapesQuotesAndKeepsDeterministicHeaders()
    {
        await using var provider = new ServiceCollection().AddMorsaCore(_root).BuildServiceProvider();
        await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
        var projectId = Guid.NewGuid();
        var node = new EntityNode { ProjectId = projectId, Type = "title", Value = "Morsa \"1.0\"", NormalizedValue = "morsa \"1.0\"", Confidence = 1 };
        var store = provider.GetRequiredService<IMorsaStore>();
        store.Add(node);
        await store.SaveChangesAsync();
        var dotPath = Path.Combine(_root, "reports", "graph.dot");
        var csvPath = Path.Combine(_root, "reports", "graph.csv");
        var exporter = provider.GetRequiredService<GraphExporter>();

        await exporter.ExportAsync(projectId, "dot", dotPath, CancellationToken.None);
        await exporter.ExportAsync(projectId, "csv", csvPath, CancellationToken.None);

        Assert.Contains("Morsa \\\"1.0\\\"", await File.ReadAllTextAsync(dotPath));
        Assert.StartsWith("record,id,type,value,from,to\n", (await File.ReadAllTextAsync(csvPath)).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("\"Morsa \"\"1.0\"\"\"", await File.ReadAllTextAsync(csvPath));
    }
}
