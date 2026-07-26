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
    [InlineData("fc00::1")]
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
}

