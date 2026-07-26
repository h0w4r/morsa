using Morsa.Infrastructure.Configuration;

namespace Morsa.UnitTests;

public sealed class MorsaConfigurationTests
{
    [Fact]
    public async Task LoadAsync_MapsSnakeCaseConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"morsa-config-{Guid.NewGuid():N}.toml");
        try
        {
            await File.WriteAllTextAsync(path, """
                [network]
                requests_per_second = 7.5
                timeout_seconds = 42

                [artifacts]
                max_download_mb = 64
                sandbox = "strict"

                [security]
                redact_sensitive_values = true
                """);

            var configuration = await MorsaConfigurationLoader.LoadAsync(path);

            Assert.Equal(7.5, configuration.Network.RequestsPerSecond);
            Assert.Equal(42, configuration.Network.TimeoutSeconds);
            Assert.Equal(64, configuration.Artifacts.MaxDownloadMb);
            Assert.Equal("strict", configuration.Artifacts.Sandbox);
            Assert.True(configuration.Security.RedactSensitiveValues);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsafeBudgets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"morsa-config-{Guid.NewGuid():N}.toml");
        try
        {
            await File.WriteAllTextAsync(path, """
                [network]
                concurrency = 0
                """);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => MorsaConfigurationLoader.LoadAsync(path));
            Assert.Contains("network.concurrency", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
