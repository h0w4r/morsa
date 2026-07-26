using System.Text.Json;

namespace Morsa.PluginFixture;

/// <summary>Marker used by integration tests to locate this fixture assembly.</summary>
public sealed class Marker;

/// <summary>Minimal deterministic implementation of the morsa-plugin/1 JSONL protocol.</summary>
public static class Program
{
    public static async Task<int> Main()
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var type = document.RootElement.GetProperty("type").GetString();
            if (type == "initialize")
            {
                Console.WriteLine("{\"type\":\"initialized\",\"protocol\":\"morsa-plugin/1\"}");
            }
            else if (type == "request")
            {
                var operation = document.RootElement.GetProperty("operation").GetString();
                Console.WriteLine(JsonSerializer.Serialize(new { type = "result", ok = true, operation }));
            }
        }

        return 0;
    }
}
