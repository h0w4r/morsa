using System.Net;
using Morsa.Application.Services;
using Morsa.Domain.Common;
using Morsa.Domain.Projects;

namespace Morsa.UnitTests;

public sealed class ScopePolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    public void IsPrivate_RejectsNonPublicRanges(string value)
    {
        Assert.True(ScopePolicy.IsPrivate(IPAddress.Parse(value)));
    }

    [Fact]
    public void IsUriAllowed_AcceptsSubdomainWithinAuthorizedDomain()
    {
        var policy = new ScopePolicy();
        var scope = new[]
        {
            new ScopeEntry { ProjectId = Guid.NewGuid(), Kind = "domain", Value = "example.com", MaximumMode = ActivityMode.Active },
        };

        Assert.True(policy.IsUriAllowed(new Uri("https://docs.example.com/file.pdf"), ActivityMode.Active, scope, false));
        Assert.False(policy.IsUriAllowed(new Uri("https://example.net/file.pdf"), ActivityMode.Active, scope, false));
    }

    [Theory]
    [InlineData("https://example.com:8443/files", true)]
    [InlineData("https://example.com:8443/files/report.pdf", true)]
    [InlineData("https://example.com:8443/files-evil/report.pdf", false)]
    [InlineData("https://example.com/files/report.pdf", false)]
    [InlineData("http://example.com:8443/files/report.pdf", false)]
    [InlineData("https://sub.example.com:8443/files/report.pdf", false)]
    public void IsUriAllowed_UrlScopeEnforcesSchemePortAndPath(string target, bool expected)
    {
        var policy = new ScopePolicy();
        var scope = new[]
        {
            new ScopeEntry
            {
                ProjectId = Guid.NewGuid(),
                Kind = "url",
                Value = "https://example.com:8443/files",
                MaximumMode = ActivityMode.Active,
            },
        };

        Assert.Equal(expected, policy.IsUriAllowed(new Uri(target), ActivityMode.Active, scope, false));
    }
}
