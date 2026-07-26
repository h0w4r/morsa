using System.Diagnostics;
using Morsa.Infrastructure.Networking;

namespace Morsa.UnitTests;

[Collection("ProcessEnvironment")]
public sealed class TargetRateLimiterTests
{
    [Fact]
    public async Task WaitAsync_SameTarget_AppliesSharedPacingAcrossCalls()
    {
        var previous = Environment.GetEnvironmentVariable("MORSA_REQUESTS_PER_SECOND");
        try
        {
            Environment.SetEnvironmentVariable("MORSA_REQUESTS_PER_SECOND", "5");
            var limiter = new TargetRateLimiter();
            var target = new Uri("https://example.test/resource");
            await limiter.WaitAsync(target, CancellationToken.None);
            var timer = Stopwatch.StartNew();

            await limiter.WaitAsync(new Uri("https://example.test/other"), CancellationToken.None);

            Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(140), $"Pacing delay was only {timer.Elapsed.TotalMilliseconds:F0} ms.");
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Pacing delay unexpectedly took {timer.Elapsed}.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MORSA_REQUESTS_PER_SECOND", previous);
        }
    }
}
