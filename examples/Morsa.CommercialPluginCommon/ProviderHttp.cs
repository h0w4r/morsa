using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Morsa.CommercialPluginCommon;

/// <summary>Bounded representation of a provider response.</summary>
public sealed record ProviderHttpResponse(HttpStatusCode StatusCode, JsonNode? Body)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;
}

/// <summary>HTTP helpers shared by plugins to enforce URL and response-size controls.</summary>
public static class ProviderHttp
{
    public static Uri ReadBaseUri(string environmentVariable, string defaultValue)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        var value = string.IsNullOrWhiteSpace(configured) ? defaultValue : configured;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidOperationException("Provider base URL is invalid.");
        }

        var httpLoopback = uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host);
        if (uri.Scheme != Uri.UriSchemeHttps && !httpLoopback)
        {
            throw new InvalidOperationException("Provider base URL must use HTTPS or loopback HTTP for fixtures.");
        }

        var normalized = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + "/");
        return normalized;
    }

    public static string ReadRequiredSecret(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16_384)
            throw new InvalidOperationException("Required provider credential is unavailable.");
        return value;
    }

    public static async Task<ProviderHttpResponse> SendJsonAsync(
        HttpClient client,
        HttpRequestMessage request,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumResponseBytes)
            throw new InvalidDataException("Provider response exceeds its byte budget.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maximumResponseBytes)
                throw new InvalidDataException("Provider response exceeds its byte budget.");
            destination.Write(buffer, 0, read);
        }

        JsonNode? body = null;
        if (destination.Length > 0)
        {
            try
            {
                body = JsonNode.Parse(destination.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException)
            {
                // A provider error page must never be echoed into the protocol response.
            }
        }

        return new ProviderHttpResponse(response.StatusCode, body);
    }

    public static string ProviderErrorMessage(ProviderHttpResponse response, params string[] secrets)
    {
        var candidate = response.Body?["error"]?["message"]?.GetValue<string>() ??
                        response.Body?["error"]?.GetValue<string>();
        var message = string.IsNullOrWhiteSpace(candidate)
            ? "Provider rejected the request."
            : candidate.Length <= 512 ? candidate : candidate[..512];
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
            message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return message;
    }

    /// <summary>Redacts declared credentials even if a fixture/provider reflects them in JSON.</summary>
    public static JsonNode RedactSecrets(JsonNode node, params string[] secrets)
    {
        var filtered = secrets.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
        Redact(node);
        return node;

        void Redact(JsonNode? current)
        {
            if (current is JsonObject jsonObject)
            {
                foreach (var property in jsonObject.ToArray())
                {
                    if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        jsonObject[property.Key] = RedactText(text);
                    else
                        Redact(property.Value);
                }
            }
            else if (current is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                        array[index] = RedactText(text);
                    else
                        Redact(array[index]);
                }
            }
        }

        string RedactText(string value)
        {
            foreach (var secret in filtered) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return value;
        }
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
