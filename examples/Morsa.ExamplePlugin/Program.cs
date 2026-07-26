using System.Text.Json;

namespace Morsa.ExamplePlugin;

/// <summary>Minimal external plugin that demonstrates the morsa-plugin/1 JSONL protocol.</summary>
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
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "initialized",
                    protocol = "morsa-plugin/1",
                    plugin_id = "morsa.example.echo",
                    version = "1.0.0",
                })).ConfigureAwait(false);
                continue;
            }

            if (type == "request")
            {
                var id = document.RootElement.GetProperty("id").GetString();
                var operation = document.RootElement.GetProperty("operation").GetString();
                var input = document.RootElement.GetProperty("input").Clone();
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "result",
                    id,
                    operation,
                    output = input,
                })).ConfigureAwait(false);
                return 0;
            }
        }

        return 0;
    }
}
