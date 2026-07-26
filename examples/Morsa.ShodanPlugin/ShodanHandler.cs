using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Morsa.CommercialPluginCommon;

namespace Morsa.ShodanPlugin;

/// <summary>Implements the bounded Shodan host-information lookup operation.</summary>
public sealed class ShodanHandler(HttpClient client, Uri? fixtureBaseUri = null, string? fixtureApiKey = null) : IPluginOperationHandler
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumServices = 512;

    public Task<OperationResult> HandleAsync(string operation, JsonElement input, CancellationToken cancellationToken) =>
        operation == "host_lookup"
            ? HostLookupAsync(input, cancellationToken)
            : Task.FromResult(OperationResult.Failure("operation_unsupported", "Supported operation is host_lookup."));

    private async Task<OperationResult> HostLookupAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("ip", out var ipNode) || ipNode.ValueKind != JsonValueKind.String ||
            !IPAddress.TryParse(ipNode.GetString(), out var address))
            return OperationResult.Failure("ip_invalid", "ip must be an IPv4 or IPv6 literal.");
        if (!TryReadOptionalBoolean(input, "history", out var history) || !TryReadOptionalBoolean(input, "minify", out var minify))
            return OperationResult.Failure("input_invalid", "history and minify must be booleans when supplied.");
        if (!TryReadConfiguration(out var baseUri, out var apiKey, out var configurationError)) return configurationError!;

        var query = $"key={Uri.EscapeDataString(apiKey)}&history={history.ToString().ToLowerInvariant()}&minify={minify.ToString().ToLowerInvariant()}";
        var relative = $"shodan/host/{Uri.EscapeDataString(address.ToString())}?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, relative));
        ProviderHttpResponse response;
        try
        {
            response = await ProviderHttp.SendJsonAsync(client, request, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            // Never surface HttpRequestException.Message because Shodan authenticates in the query string.
            return OperationResult.Failure("provider_unavailable", "Shodan request failed or exceeded its response budget.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return OperationResult.Success(new JsonObject { ["provider"] = "shodan", ["found"] = false, ["ip"] = address.ToString() });
        if (!response.IsSuccess)
            return OperationResult.Failure("provider_http_error", ProviderHttp.ProviderErrorMessage(response, apiKey), (int)response.StatusCode);
        return OperationResult.Success(ProviderHttp.RedactSecrets(NormalizeHost(response.Body, address.ToString()), apiKey));
    }

    private static JsonObject NormalizeHost(JsonNode? body, string requestedIp)
    {
        var result = new JsonObject
        {
            ["provider"] = "shodan",
            ["found"] = true,
            ["requested_ip"] = requestedIp,
        };
        foreach (var name in new[]
                 {
                     "ip_str", "org", "isp", "asn", "os", "last_update", "country_code", "country_name",
                     "city", "region_code", "latitude", "longitude",
                 })
        {
            if (body?[name] is { } value) result[name] = value.DeepClone();
        }

        foreach (var (name, limit) in new[]
                 {
                     ("ports", 2_048), ("hostnames", 256), ("domains", 256), ("tags", 256), ("cpe", 512),
                 })
        {
            if (body?[name] is JsonArray array) result[name] = CloneArray(array, limit);
        }

        if (body?["vulns"] is JsonArray vulnerabilities)
            result["vulns"] = CloneArray(vulnerabilities, 2_048);
        else if (body?["vulns"] is JsonObject vulnerabilityMap)
        {
            var bounded = new JsonObject();
            foreach (var item in vulnerabilityMap.Take(2_048)) bounded[item.Key] = item.Value?.DeepClone();
            result["vulns"] = bounded;
        }

        if (body?["data"] is JsonArray services)
        {
            var normalizedServices = new JsonArray();
            foreach (var service in services.OfType<JsonObject>().Take(MaximumServices))
            {
                var normalized = new JsonObject();
                foreach (var name in new[] { "port", "transport", "product", "version", "timestamp", "hash", "_shodan", "ssl" })
                    if (service[name] is { } value) normalized[name] = value.DeepClone();
                if (service["hostnames"] is JsonArray hostnames) normalized["hostnames"] = CloneArray(hostnames, 64);
                if (service["cpe"] is JsonArray cpe) normalized["cpe"] = CloneArray(cpe, 128);
                if (service["data"] is JsonValue bannerNode && bannerNode.TryGetValue<string>(out var banner))
                    normalized["banner"] = banner.Length <= 4_096 ? banner : banner[..4_096];
                normalizedServices.Add(normalized);
            }

            result["services"] = normalizedServices;
            result["services_truncated"] = services.Count > MaximumServices;
        }

        return result;
    }

    private static JsonArray CloneArray(JsonArray source, int maximum) =>
        new(source.Take(maximum).Select(item => item?.DeepClone()).ToArray());

    private bool TryReadConfiguration(out Uri baseUri, out string apiKey, out OperationResult? error)
    {
        try
        {
            baseUri = fixtureBaseUri ?? ProviderHttp.ReadBaseUri("SHODAN_API_BASE_URL", "https://api.shodan.io/");
            apiKey = fixtureApiKey ?? ProviderHttp.ReadRequiredSecret("SHODAN_API_KEY");
            error = null;
            return true;
        }
        catch
        {
            baseUri = null!;
            apiKey = string.Empty;
            error = OperationResult.Failure("configuration_invalid", "Shodan API key or base URL is unavailable or invalid.");
            return false;
        }
    }

    private static bool TryReadOptionalBoolean(JsonElement input, string name, out bool value)
    {
        value = false;
        if (!input.TryGetProperty(name, out var node)) return true;
        if (node.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = node.GetBoolean();
        return true;
    }
}
