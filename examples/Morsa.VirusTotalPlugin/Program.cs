using Morsa.CommercialPluginCommon;

namespace Morsa.VirusTotalPlugin;

/// <summary>stdio composition root for the optional VirusTotal adapter.</summary>
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Morsa-VirusTotal-Plugin/1.0");
        return await PluginProtocolHost.RunAsync(
            new PluginIdentity("morsa.provider.virustotal", "1.0.0"),
            new VirusTotalHandler(client),
            Console.In,
            Console.Out,
            cancellation.Token).ConfigureAwait(false);
    }
}
