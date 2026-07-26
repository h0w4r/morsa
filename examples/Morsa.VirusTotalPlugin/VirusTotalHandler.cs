using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Morsa.CommercialPluginCommon;

namespace Morsa.VirusTotalPlugin;

/// <summary>Implements bounded VirusTotal v3 hash lookup and explicitly authorized upload.</summary>
public sealed class VirusTotalHandler(HttpClient client, Uri? fixtureBaseUri = null, string? fixtureApiKey = null) : IPluginOperationHandler
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const long MaximumDirectUploadBytes = 32L * 1024 * 1024;

    public Task<OperationResult> HandleAsync(string operation, JsonElement input, CancellationToken cancellationToken) =>
        operation switch
        {
            "hash_lookup" => HashLookupAsync(input, cancellationToken),
            "upload" => UploadAsync(input, cancellationToken),
            _ => Task.FromResult(OperationResult.Failure("operation_unsupported", "Supported operations are hash_lookup and upload.")),
        };

    private async Task<OperationResult> HashLookupAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("hash", out var hashNode) || hashNode.ValueKind != JsonValueKind.String)
            return OperationResult.Failure("input_invalid", "hash is required.");
        var hash = hashNode.GetString()!.Trim().ToLowerInvariant();
        if (hash.Length is not (32 or 40 or 64) || !hash.All(Uri.IsHexDigit))
            return OperationResult.Failure("hash_invalid", "hash must be an MD5, SHA-1 or SHA-256 hexadecimal value.");
        if (!TryReadConfiguration(out var baseUri, out var apiKey, out var configurationError)) return configurationError!;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"files/{hash}"));
        request.Headers.TryAddWithoutValidation("x-apikey", apiKey);
        ProviderHttpResponse response;
        try
        {
            response = await ProviderHttp.SendJsonAsync(client, request, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            return OperationResult.Failure("provider_unavailable", "VirusTotal request failed or exceeded its response budget.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return OperationResult.Success(new JsonObject { ["provider"] = "virustotal", ["found"] = false, ["hash"] = hash });
        if (!response.IsSuccess)
            return OperationResult.Failure("provider_http_error", ProviderHttp.ProviderErrorMessage(response, apiKey), (int)response.StatusCode);
        return OperationResult.Success(ProviderHttp.RedactSecrets(NormalizeFile(response.Body, hash), apiKey));
    }

    private async Task<OperationResult> UploadAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("explicit_upload", out var explicitNode) || explicitNode.ValueKind is not JsonValueKind.True)
            return OperationResult.Failure("upload_confirmation_required", "upload requires explicit_upload=true.");
        if (!input.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathNode.GetString()))
            return OperationResult.Failure("input_invalid", "path is required for upload.");
        if (!TryReadConfiguration(out var baseUri, out var apiKey, out var configurationError)) return configurationError!;

        string fullPath;
        FileInfo file;
        try
        {
            fullPath = Path.GetFullPath(pathNode.GetString()!);
            file = new FileInfo(fullPath);
            if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return OperationResult.Failure("file_invalid", "Upload source must be an existing regular file, not a link.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return OperationResult.Failure("file_invalid", "Upload source could not be accessed.");
        }

        if (file.Length is <= 0 or > MaximumDirectUploadBytes)
            return OperationResult.Failure("file_size_invalid", "Direct upload accepts files from 1 byte through 32 MiB.");

        try
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var multipart = new MultipartFormDataContent();
            using var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(streamContent, "file", Path.GetFileName(fullPath));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "files")) { Content = multipart };
            request.Headers.TryAddWithoutValidation("x-apikey", apiKey);
            var response = await ProviderHttp.SendJsonAsync(client, request, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
                return OperationResult.Failure("provider_http_error", ProviderHttp.ProviderErrorMessage(response, apiKey), (int)response.StatusCode);
            return OperationResult.Success(ProviderHttp.RedactSecrets(NormalizeUpload(response.Body, file.Name, file.Length), apiKey));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            return OperationResult.Failure("provider_unavailable", "VirusTotal upload failed or exceeded its response budget.");
        }
    }

    private static JsonObject NormalizeFile(JsonNode? body, string requestedHash)
    {
        var data = body?["data"];
        var attributes = data?["attributes"];
        var result = new JsonObject
        {
            ["provider"] = "virustotal",
            ["found"] = true,
            ["requested_hash"] = requestedHash,
            ["id"] = Clone(data?["id"]),
            ["object_type"] = Clone(data?["type"]),
        };
        foreach (var name in new[]
                 {
                     "sha256", "sha1", "md5", "size", "type_description", "meaningful_name", "reputation",
                     "last_analysis_date", "last_submission_date", "times_submitted", "last_analysis_stats",
                 })
        {
            if (attributes?[name] is { } value) result[name] = value.DeepClone();
        }

        return result;
    }

    private static JsonObject NormalizeUpload(JsonNode? body, string fileName, long size) => new()
    {
        ["provider"] = "virustotal",
        ["uploaded"] = true,
        ["file_name"] = fileName,
        ["size"] = size,
        ["analysis_id"] = Clone(body?["data"]?["id"]),
        ["object_type"] = Clone(body?["data"]?["type"]),
    };

    private bool TryReadConfiguration(out Uri baseUri, out string apiKey, out OperationResult? error)
    {
        try
        {
            baseUri = fixtureBaseUri ?? ProviderHttp.ReadBaseUri("VT_API_BASE_URL", "https://www.virustotal.com/api/v3/");
            apiKey = fixtureApiKey ?? ProviderHttp.ReadRequiredSecret("VT_API_KEY");
            error = null;
            return true;
        }
        catch
        {
            baseUri = null!;
            apiKey = string.Empty;
            error = OperationResult.Failure("configuration_invalid", "VirusTotal API key or base URL is unavailable or invalid.");
            return false;
        }
    }

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();
}
