using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Morsa.CommercialPluginCommon;
using Morsa.ShodanPlugin;
using Morsa.VirusTotalPlugin;
using Xunit;

namespace Morsa.CommercialPlugins.Tests;

public sealed class CommercialPluginTests
{
    private const string Secret = "fixture-secret-never-return";

    [Fact]
    public async Task VirusTotal_HashLookup_UsesHeaderAndReturnsBoundedNormalizedReport()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpHandler(async request =>
        {
            captured = CloneRequest(request);
            return await JsonResponseAsync(HttpStatusCode.OK, """
                {"data":{"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","type":"file","attributes":{"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":123,"meaningful_name":"sample.bin","last_analysis_stats":{"malicious":2,"undetected":60},"ignored_field":"not-copied"}}}
                """);
        });
        var plugin = new VirusTotalHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18080/api/v3/"), Secret);

        var result = await plugin.HandleAsync("hash_lookup", Input("""{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal(Secret, Assert.Single(captured.Headers.GetValues("x-apikey")));
        var output = result.Output!.ToJsonString();
        Assert.Contains("last_analysis_stats", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored_field", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirusTotal_Upload_RequiresExplicitFlagBeforeAnyHttpRequest()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            throw new InvalidOperationException("HTTP should not be reached.");
        });
        var plugin = new VirusTotalHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18080/api/v3/"), Secret);

        var result = await plugin.HandleAsync("upload", Input("""{"path":"sample.bin"}"""), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("upload_confirmation_required", result.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task VirusTotal_ProviderError_ReflectingCredentialIsRedacted()
    {
        var handler = new StubHttpHandler(_ => JsonResponseAsync(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"credential " + Secret + " was rejected\"}}"));
        var plugin = new VirusTotalHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18080/api/v3/"), Secret);

        var result = await plugin.HandleAsync("hash_lookup", Input("""{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("[redacted]", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VirusTotal_ExplicitUpload_SendsMultipartWithoutReturningLocalPathOrSecret()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"morsa-vt-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(temporary, [1, 2, 3, 4]);
        try
        {
            byte[]? body = null;
            var handler = new StubHttpHandler(async request =>
            {
                body = await request.Content!.ReadAsByteArrayAsync();
                return await JsonResponseAsync(HttpStatusCode.OK, """{"data":{"id":"analysis-1","type":"analysis"}}""");
            });
            var plugin = new VirusTotalHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18080/api/v3/"), Secret);
            var input = new JsonObject { ["path"] = temporary, ["explicit_upload"] = true };

            var result = await plugin.HandleAsync("upload", Input(input.ToJsonString()), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(body);
            Assert.Contains("analysis-1", result.Output!.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(temporary, result.Output!.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, result.Output!.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [Fact]
    public async Task Shodan_HostLookup_NormalizesServicesAndNeverReturnsQuerySecret()
    {
        Uri? requestUri = null;
        var handler = new StubHttpHandler(async request =>
        {
            requestUri = request.RequestUri;
            return await JsonResponseAsync(HttpStatusCode.OK, """
                {"ip_str":"203.0.113.10","org":"Morsa fixture-secret-never-return ISP","ports":[22,443],"hostnames":["host.example"],"data":[{"port":443,"transport":"tcp","product":"nginx","data":"HTTP banner","unbounded":"drop-me"}]}
                """);
        });
        var plugin = new ShodanHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18081/"), Secret);

        var result = await plugin.HandleAsync("host_lookup", Input("""{"ip":"203.0.113.10","history":true,"minify":false}"""), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains($"key={Secret}", requestUri!.Query, StringComparison.Ordinal);
        var output = result.Output!.ToJsonString();
        Assert.Contains("nginx", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unbounded", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shodan_InvalidIp_IsRejectedBeforeHttp()
    {
        var calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            throw new InvalidOperationException("HTTP should not be reached.");
        });
        var plugin = new ShodanHandler(new HttpClient(handler), new Uri("http://127.0.0.1:18081/"), Secret);

        var result = await plugin.HandleAsync("host_lookup", Input("""{"ip":"example.com"}"""), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ip_invalid", result.ErrorCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ProtocolHost_EmitsOnlyInitializedAndCorrelatedResultLines()
    {
        const string messages = """
            {"type":"initialize","protocol":"morsa-plugin/1"}
            {"type":"request","id":"request-1","operation":"echo","input":{"value":7}}
            """;
        using var input = new StringReader(messages);
        using var output = new StringWriter();

        var exitCode = await PluginProtocolHost.RunAsync(
            new PluginIdentity("morsa.test", "1.0.0"),
            new EchoHandler(),
            input,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("initialized", JsonNode.Parse(lines[0])!["type"]!.GetValue<string>());
        Assert.Equal("request-1", JsonNode.Parse(lines[1])!["id"]!.GetValue<string>());
    }

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Task<HttpResponseMessage> JsonResponseAsync(HttpStatusCode statusCode, string json) =>
        Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private sealed class EchoHandler : IPluginOperationHandler
    {
        public Task<OperationResult> HandleAsync(string operation, JsonElement input, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(JsonNode.Parse(input.GetRawText())!));
    }
}
