using Morsa.Infrastructure.Networking;

namespace Morsa.UnitTests;

public sealed class CompositeProxySourceTests
{
    [Fact]
    public async Task LoadAsync_CommandPrefix_IsDispatchedBeforeGenericUriParsing()
    {
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "whoami.exe")
            : "/usr/bin/whoami";
        if (!File.Exists(executable))
        {
            // This assertion keeps the test explicit on unusual minimal distributions.
            Assert.Fail($"Deterministic identity executable was not found at {executable}.");
        }

        var source = new CompositeProxySource();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in source.LoadAsync($"command:{executable}", CancellationToken.None))
            {
                // `whoami` output is deliberately not a proxy candidate.
            }
        });

        Assert.StartsWith("Invalid proxy URI:", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Remote proxy sources", exception.Message, StringComparison.Ordinal);
    }
}
