using System.Text;
using System.Text.Json;
using System.Web;
using HtmlAgilityPack;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Networking;

namespace Morsa.Infrastructure.Discovery;

/// <summary>Shared parsing and canonicalization helpers for discovery providers.</summary>
public static class DiscoveryUtilities
{
    public static string Canonicalize(string value)
    {
        var uri = new Uri(value);
        var builder = new UriBuilder(uri)
        {
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
        };
        if ((builder.Scheme == "http" && builder.Port == 80) || (builder.Scheme == "https" && builder.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    public static IEnumerable<(string Url, string? Title)> ExtractLinks(string html, Uri baseUri)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var nodes = document.DocumentNode.SelectNodes("//a[@href]");
        if (nodes is null)
        {
            yield break;
        }

        foreach (var node in nodes)
        {
            var href = HttpUtility.HtmlDecode(node.GetAttributeValue("href", string.Empty));
            if (Uri.TryCreate(baseUri, href, out var uri) && uri.Scheme is "http" or "https")
            {
                yield return (uri.AbsoluteUri, HttpUtility.HtmlDecode(node.InnerText).Trim());
            }
        }
    }

    /// <summary>Extracts URLs from XML sitemaps without enabling external entities.</summary>
    public static IEnumerable<string> ExtractSitemapLocations(string xml)
    {
        var document = new System.Xml.XmlDocument { XmlResolver = null };
        document.LoadXml(xml);
        var nodes = document.SelectNodes("//*[local-name()='loc']");
        if (nodes is null)
        {
            yield break;
        }

        foreach (System.Xml.XmlNode node in nodes)
        {
            if (node.InnerText.Trim() is { Length: > 0 } value &&
                Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                yield return uri.AbsoluteUri;
            }
        }
    }
}

/// <summary>Unofficial DuckDuckGo HTML provider with explicit challenge handling.</summary>
public sealed class DuckDuckGoSearchProvider(RotatingHttpClient http) : ISearchProvider
{
    public string Id => "duckduckgo";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealth(true, "configured"));

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var type in query.FileTypes)
        {
            var expression = $"site:{query.Target} filetype:{type}";
            var uri = new Uri($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(expression)}");
            var requestContext = new NetworkRequestContext(context.RunId, context.TaskId, $"ddg:{context.SessionKey}", uri, "discovery", Id);
            var result = await http.FetchAsync(uri, context.ProxyPool, requestContext, 4 * 1024 * 1024, cancellationToken)
                .ConfigureAwait(false);
            var html = Encoding.UTF8.GetString(result.Content);
            var count = 0;
            foreach (var link in DiscoveryUtilities.ExtractLinks(html, uri))
            {
                var candidate = DecodeDuckDuckGoUrl(link.Url);
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) ||
                    !candidateUri.Host.EndsWith(query.Target, StringComparison.OrdinalIgnoreCase) ||
                    !query.FileTypes.Any(ext => candidateUri.AbsolutePath.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return new SearchResult(candidateUri.AbsoluteUri, link.Title, null, Id, expression, DateTimeOffset.UtcNow);
                if (++count >= query.MaxResults)
                {
                    yield break;
                }
            }
        }
    }

    private static string DecodeDuckDuckGoUrl(string value)
    {
        var uri = new Uri(value);
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query["uddg"] is { Length: > 0 } target ? target : value;
    }
}

/// <summary>SearXNG JSON provider using a user-owned instance.</summary>
public sealed class SearXngSearchProvider(RotatingHttpClient http) : ISearchProvider
{
    private readonly string? _baseUrl = Environment.GetEnvironmentVariable("MORSA_SEARXNG_URL");

    public string Id => "searxng";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealth(_baseUrl is not null, _baseUrl is null ? "misconfigured" : "configured"));

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_baseUrl is null)
        {
            yield break;
        }

        foreach (var type in query.FileTypes)
        {
            var expression = $"site:{query.Target} filetype:{type}";
            var uri = new Uri($"{_baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(expression)}&format=json");
            var result = await http.FetchAsync(
                uri,
                context.ProxyPool,
                new NetworkRequestContext(context.RunId, context.TaskId, $"searx:{context.SessionKey}", uri, "discovery", Id),
                8 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(result.Content);
            foreach (var item in json.RootElement.GetProperty("results").EnumerateArray().Take(query.MaxResults))
            {
                if (item.TryGetProperty("url", out var url))
                {
                    yield return new SearchResult(
                        url.GetString()!,
                        item.TryGetProperty("title", out var title) ? title.GetString() : null,
                        item.TryGetProperty("content", out var content) ? content.GetString() : null,
                        Id,
                        expression,
                        DateTimeOffset.UtcNow);
                }
            }
        }
    }
}

/// <summary>Common Crawl CDX provider selected from the current index catalogue.</summary>
public sealed class CommonCrawlSearchProvider(RotatingHttpClient http) : ISearchProvider
{
    public string Id => "commoncrawl";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealth(true, "configured"));

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var catalogue = new Uri("https://index.commoncrawl.org/collinfo.json");
        var catalogueResult = await http.FetchAsync(
            catalogue,
            context.ProxyPool,
            new NetworkRequestContext(context.RunId, context.TaskId, $"cc-catalog:{context.SessionKey}", catalogue, "discovery", Id),
            2 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        using var catalogueJson = JsonDocument.Parse(catalogueResult.Content);
        var api = catalogueJson.RootElement[0].GetProperty("cdx-api").GetString()!;
        var filter = string.Join('|', query.FileTypes.Select(type => $"\\.{type}$"));
        var uri = new Uri($"{api}?url=*.{query.Target}/*&output=json&filter=status:200&filter=url:({Uri.EscapeDataString(filter)})&collapse=urlkey&pageSize={query.MaxResults}");
        var result = await http.FetchAsync(
            uri,
            context.ProxyPool,
            new NetworkRequestContext(context.RunId, context.TaskId, $"cc:{context.SessionKey}", uri, "discovery", Id),
            16 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        foreach (var line in Encoding.UTF8.GetString(result.Content).Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(query.MaxResults))
        {
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("url", out var url))
            {
                yield return new SearchResult(url.GetString()!, null, null, Id, query.Target, DateTimeOffset.UtcNow);
            }
        }
    }
}

/// <summary>Active direct crawler for home pages and sitemaps, bounded to one level.</summary>
public sealed class DirectCrawlerSearchProvider(RotatingHttpClient http) : ISearchProvider
{
    public string Id => "direct-crawler";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealth(true, "configured"));

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var roots = new[] { new Uri($"https://{query.Target}/"), new Uri($"https://{query.Target}/sitemap.xml") };
        var emitted = 0;
        foreach (var root in roots)
        {
            HttpFetchResult result;
            try
            {
                result = await http.FetchAsync(
                    root,
                    context.ProxyPool,
                    new NetworkRequestContext(context.RunId, context.TaskId, $"crawl:{context.SessionKey}", root, "crawler", Id),
                    8 * 1024 * 1024,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            var body = Encoding.UTF8.GetString(result.Content);
            var links = root.AbsolutePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? DiscoveryUtilities.ExtractSitemapLocations(body).Select(value => (Url: value, Title: (string?)null))
                : DiscoveryUtilities.ExtractLinks(body, root);
            foreach (var link in links)
            {
                if (!query.FileTypes.Any(type => new Uri(link.Url).AbsolutePath.EndsWith($".{type}", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return new SearchResult(link.Url, link.Title, null, Id, root.AbsoluteUri, DateTimeOffset.UtcNow);
                if (++emitted >= query.MaxResults)
                {
                    yield break;
                }
            }
        }
    }
}
