using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Morsa.Application.Abstractions;
using Morsa.Domain.Projects;
using Morsa.Infrastructure;
using Morsa.Infrastructure.Discovery;

namespace Morsa.IntegrationTests;

public sealed class DiscoveryImportIntegrationTests
{
    public static IEnumerable<object[]> Formats()
    {
        yield return ["text", "HTTPS://EXAMPLE.test:443/a.pdf#fragment\nhttps://example.test/a.pdf\nftp://example.test/ignored"];
        yield return ["csv", "url,title,snippet\n\"https://example.test/a.pdf\",\"Report, Q1\",\"A \"\"quoted\"\" result\"\nhttps://example.test/a.pdf,duplicate,duplicate"];
        yield return ["json", "[{\"url\":\"https://example.test/a.pdf\",\"title\":\"Report\"},\"https://example.test/a.pdf#again\"]"];
        yield return ["ndjson", "{\"url\":\"https://example.test/a.pdf\",\"title\":\"Report\"}\n{\"url\":\"https://example.test/a.pdf#again\"}"];
        yield return ["har", "{\"log\":{\"entries\":[{\"request\":{\"url\":\"https://example.test/a.pdf\"}},{\"request\":{\"url\":\"https://example.test/a.pdf#again\"}}]}}"];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ImportAsync_SupportedFormat_CanonicalizesAndDeduplicates(string format, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-import", Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new ServiceCollection().AddMorsaCore(root).BuildServiceProvider();
            await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
            var store = provider.GetRequiredService<IMorsaStore>();
            var project = new MorsaProject { Name = "import", RootPath = root };
            store.Add(project);
            await store.SaveChangesAsync();
            var path = Path.Combine(root, $"source.{format}");
            await File.WriteAllTextAsync(path, content);

            var added = await provider.GetRequiredService<DiscoveryImportService>()
                .ImportAsync(project.Id, Guid.NewGuid(), path, format, 100, CancellationToken.None);

            Assert.Equal(1, added);
            var resource = Assert.Single(store.DiscoveredResources);
            Assert.Equal("https://example.test/a.pdf", resource.CanonicalUrl);
            Assert.Equal($"import:{format}", resource.ProviderId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_MaximumBudget_LimitsPersistedRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-import", Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = new ServiceCollection().AddMorsaCore(root).BuildServiceProvider();
            await provider.GetRequiredService<IStoreInitializer>().InitializeAsync();
            var store = provider.GetRequiredService<IMorsaStore>();
            var project = new MorsaProject { Name = "budget", RootPath = root };
            store.Add(project);
            await store.SaveChangesAsync();
            var path = Path.Combine(root, "source.txt");
            await File.WriteAllLinesAsync(path, Enumerable.Range(1, 10).Select(index => $"https://example.test/{index}.pdf"));

            var added = await provider.GetRequiredService<DiscoveryImportService>()
                .ImportAsync(project.Id, Guid.NewGuid(), path, "text", 3, CancellationToken.None);

            Assert.Equal(3, added);
            Assert.Equal(3, store.DiscoveredResources.Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
