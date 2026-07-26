using System.Text.Json;
using Morsa.Cli.Runtime;

namespace Morsa.UnitTests;

[Collection("ProcessEnvironment")]
public sealed class CliJsonOutputTests
{
    [Fact]
    public void Write_MachineOutput_UsesSnakeCaseVersionedSingleLineEnvelope()
    {
        var original = Console.Out;
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            var output = new CliOutput();
            output.Write(new { LongPropertyName = "value" }, json: true, runId: "run-1", coverage: "complete");
            output.WriteError("fixture.error", "bounded failure");
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("1", document.RootElement.GetProperty("schema_version").GetString());
        }

        using var success = JsonDocument.Parse(lines[0]);
        Assert.True(success.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("value", success.RootElement.GetProperty("data").GetProperty("long_property_name").GetString());
        Assert.Equal("run-1", success.RootElement.GetProperty("run_id").GetString());
        Assert.DoesNotContain("LongPropertyName", lines[0], StringComparison.Ordinal);

        using var failure = JsonDocument.Parse(lines[1]);
        Assert.False(failure.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("fixture.error", failure.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void Write_Ndjson_EmitsOneVersionedEnvelopePerCollectionItem()
    {
        var original = Console.Out;
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            var output = new CliOutput(configuration: null, ndjsonRequested: true);
            output.Write(new[] { new { Id = 1 }, new { Id = 2 } }, json: true);
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal([1, 2], lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("id").GetInt32()).ToArray());
        Assert.All(lines, line => Assert.Equal("1", JsonDocument.Parse(line).RootElement.GetProperty("schema_version").GetString()));
    }
}
