using Morsa.CommercialPluginCommon;

namespace Morsa.ShodanPlugin;

/// <summary>stdio composition root for the optional Shodan adapter.</summary>
public static class Program
{
    public static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Morsa-Shodan-Plugin/1.0");
        return await PluginProtocolHost.RunAsync(
            new PluginIdentity("morsa.provider.shodan", "1.0.0"),
            new ShodanHandler(client),
            Console.In,
            Console.Out,
            cancellation.Token).ConfigureAwait(false);
    }
}
