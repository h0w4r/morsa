using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;

namespace Morsa.UnitTests;

[Collection("ProcessEnvironment")]
public sealed class EnvironmentProxyResolverTests
{
    [Fact]
    public void Resolve_HttpsDestination_PrefersHttpsProxyAndClassifiesSocks5hDns()
    {
        using var environment = new ProxyEnvironmentScope();
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "socks5h://proxy.example.test:1080");
        Environment.SetEnvironmentVariable("HTTP_PROXY", "http://fallback.example.test:8080");
        Environment.SetEnvironmentVariable("ALL_PROXY", null);
        Environment.SetEnvironmentVariable("NO_PROXY", null);

        var endpoint = new EnvironmentProxyResolver().Resolve(new Uri("https://target.example.test/document.pdf"));

        Assert.NotNull(endpoint);
        Assert.Equal("socks5h://proxy.example.test:1080/", endpoint.Uri);
        Assert.Equal(ProxyProtocol.Socks5Host, endpoint.Protocol);
        Assert.Equal(ProxyDnsMode.Remote, endpoint.DnsMode);
    }

    [Theory]
    [InlineData("example.test", "https://example.test/")]
    [InlineData(".example.test", "https://sub.example.test/")]
    [InlineData("example.test:443", "https://example.test/")]
    [InlineData("*", "https://anywhere.test/")]
    public void Resolve_NoProxyMatch_BypassesConfiguredProxy(string noProxy, string destination)
    {
        using var environment = new ProxyEnvironmentScope();
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy.example.test:8080");
        Environment.SetEnvironmentVariable("NO_PROXY", noProxy);

        Assert.Null(new EnvironmentProxyResolver().Resolve(new Uri(destination)));
    }

    /// <summary>Restores process-wide variables even when an assertion fails.</summary>
    private sealed class ProxyEnvironmentScope : IDisposable
    {
        private static readonly string[] Names = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];
        private readonly Dictionary<string, string?> _values = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        public void Dispose()
        {
            foreach (var item in _values) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }
}

[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
