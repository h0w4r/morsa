using Morsa.Domain.Networking;
using Morsa.Infrastructure.Networking;

namespace Morsa.UnitTests;

public sealed class ProxySelectionTests
{
    private readonly ProxyEndpoint[] _endpoints =
    [
        Endpoint("http://127.0.0.1:8001", 50),
        Endpoint("http://127.0.0.1:8002", 10),
        Endpoint("http://127.0.0.1:8003", 30),
    ];

    [Fact]
    public void Sticky_ReturnsSameEndpointForSameSession()
    {
        var engine = new ProxySelectionEngine();
        var first = engine.Select(ProxySelectionPolicy.Sticky, _endpoints, "provider:session", 0);
        var second = engine.Select(ProxySelectionPolicy.Sticky, _endpoints, "provider:session", 999);

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public void LeastLatency_SelectsFastestMeasuredEndpoint()
    {
        var selected = new ProxySelectionEngine().Select(
            ProxySelectionPolicy.LeastLatency,
            _endpoints,
            "session",
            0);

        Assert.Equal("http://127.0.0.1:8002/", selected!.Uri);
    }

    [Theory]
    [InlineData("http://proxy.example:8080", ProxyProtocol.Http, ProxyDnsMode.Local)]
    [InlineData("socks5h://proxy.example:1080", ProxyProtocol.Socks5Host, ProxyDnsMode.Remote)]
    public void ProxyParser_ClassifiesProtocolAndDns(string uri, ProxyProtocol protocol, ProxyDnsMode dns)
    {
        var candidate = FileProxySource.Parse(uri, null, 1, []);
        Assert.Equal(protocol, candidate.Protocol);
        Assert.Equal(dns, candidate.DnsMode);
    }

    [Fact]
    public void ProxyParser_RejectsInlineCredentials()
    {
        Assert.Throws<InvalidDataException>(() =>
            FileProxySource.Parse("http://user:pass@proxy.example:8080", null, 1, []));
    }

    private static ProxyEndpoint Endpoint(string uri, double latency) => new()
    {
        PoolId = Guid.NewGuid(),
        Uri = new Uri(uri).ToString(),
        Protocol = ProxyProtocol.Http,
        DnsMode = ProxyDnsMode.Local,
        EwmaLatencyMs = latency,
    };
}

