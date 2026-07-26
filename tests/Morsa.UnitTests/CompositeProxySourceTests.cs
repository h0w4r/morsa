using Morsa.Infrastructure.Networking;

namespace Morsa.UnitTests;

public sealed class CompositeProxySourceTests
{
    [Fact]
    public async Task LoadAsync_AbsoluteLocalPath_LoadsFileBeforeGenericUriParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"morsa-proxies-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "http://127.0.0.1:19082\n");
        try
        {
            var candidates = new List<Morsa.Application.Abstractions.ProxyCandidate>();
            await foreach (var candidate in new CompositeProxySource().LoadAsync(path, CancellationToken.None))
            {
                candidates.Add(candidate);
            }

            var selected = Assert.Single(candidates);
            Assert.Equal("http://127.0.0.1:19082/", selected.Uri.AbsoluteUri);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
