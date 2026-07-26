using Morsa.Infrastructure.Discovery;

namespace Morsa.UnitTests;

public sealed class DiscoveryUtilitiesTests
{
    [Fact]
    public void Canonicalize_NormalizesIdnDefaultPortAndFragment()
    {
        var result = DiscoveryUtilities.Canonicalize("HTTPS://BÜCHER.example:443/a%20b?q=1#fragment");

        Assert.Equal("https://xn--bcher-kva.example/a%20b?q=1", result);
    }

    [Fact]
    public void ExtractLinks_ResolvesRelativeLinksAndSkipsNonHttpSchemes()
    {
        const string html = "<a href='/docs/a.pdf'>A</a><a href='mailto:test@example.test'>mail</a><a href='javascript:alert(1)'>bad</a>";

        var links = DiscoveryUtilities.ExtractLinks(html, new Uri("https://example.test/root/")).ToArray();

        var link = Assert.Single(links);
        Assert.Equal("https://example.test/docs/a.pdf", link.Url);
        Assert.Equal("A", link.Title);
    }

    [Fact]
    public void ExtractSitemapLocations_NamespacedXml_ReturnsOnlyHttpUrls()
    {
        const string xml = "<urlset xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'><url><loc>https://example.test/a.pdf</loc></url><url><loc>file:///etc/passwd</loc></url></urlset>";

        var locations = DiscoveryUtilities.ExtractSitemapLocations(xml).ToArray();

        Assert.Equal(["https://example.test/a.pdf"], locations);
    }

    [Fact]
    public void ExtractSitemapLocations_ExternalEntity_IsNotResolved()
    {
        const string xml = "<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><urlset><loc>&xxe;</loc></urlset>";

        Assert.Empty(DiscoveryUtilities.ExtractSitemapLocations(xml));
    }
}
