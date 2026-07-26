using System.Text.Json;
using System.Text.Json.Nodes;

namespace Morsa.CommercialPluginCommon;

/// <summary>Identity returned during the morsa-plugin/1 initialization handshake.</summary>
public sealed record PluginIdentity(string Id, string Version);

/// <summary>Operation boundary implemented by each commercial provider adapter.</summary>
public interface IPluginOperationHandler
{
    Task<OperationResult> HandleAsync(string operation, JsonElement input, CancellationToken cancellationToken);
}

/// <summary>Provider-neutral result that never needs to expose an exception or a secret.</summary>
public sealed record OperationResult(
    bool IsSuccess,
    JsonNode? Output,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? StatusCode = null)
{
    public static OperationResult Success(JsonNode output) => new(true, output);

    public static OperationResult Failure(string code, string message, int? statusCode = null) =>
        new(false, null, code, message, statusCode);
}

/// <summary>
/// Runs one request over the external JSONL protocol. Standard output is reserved exclusively for
/// protocol messages so diagnostics can never corrupt the Morsa transport.
/// </summary>
public static class PluginProtocolHost
{
    private const int MaximumLineCharacters = 1024 * 1024;

    public static async Task<int> RunAsync(
        PluginIdentity identity,
        IPluginOperationHandler handler,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await ReadBoundedLineAsync(input, MaximumLineCharacters, cancellationToken).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("type", out var typeNode))
                {
                    await WriteErrorAsync(output, null, "protocol_invalid", "Message must contain a type.", null).ConfigureAwait(false);
                    return 2;
                }

                var type = typeNode.GetString();
                if (type == "initialize")
                {
                    var protocol = document.RootElement.TryGetProperty("protocol", out var protocolNode)
                        ? protocolNode.GetString()
                        : null;
                    if (protocol != "morsa-plugin/1")
                    {
                        await WriteErrorAsync(output, null, "protocol_unsupported", "Only morsa-plugin/1 is supported.", null).ConfigureAwait(false);
                        return 2;
                    }

                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        type = "initialized",
                        protocol = "morsa-plugin/1",
                        plugin_id = identity.Id,
                        version = identity.Version,
                    })).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (type != "request")
                {
                    await WriteErrorAsync(output, null, "protocol_invalid", "Unsupported message type.", null).ConfigureAwait(false);
                    return 2;
                }

                var id = document.RootElement.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
                var operation = document.RootElement.TryGetProperty("operation", out var operationNode) ? operationNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || string.IsNullOrWhiteSpace(operation) || operation.Length > 128 ||
                    !document.RootElement.TryGetProperty("input", out var requestInput) || requestInput.ValueKind != JsonValueKind.Object)
                {
                    await WriteErrorAsync(output, id, "request_invalid", "Request id, operation and object input are required.", null).ConfigureAwait(false);
                    return 2;
                }

                var result = await handler.HandleAsync(operation, requestInput.Clone(), cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        type = "result",
                        id,
                        operation,
                        output = result.Output,
                    })).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                await WriteErrorAsync(output, id, result.ErrorCode!, result.ErrorMessage!, result.StatusCode).ConfigureAwait(false);
                return 1;
            }

            return 0;
        }
        catch (InvalidDataException)
        {
            await WriteErrorAsync(output, null, "protocol_limit", "Protocol input exceeded its configured limit.", null).ConfigureAwait(false);
            return 2;
        }
        catch (JsonException)
        {
            await WriteErrorAsync(output, null, "json_invalid", "Protocol input is not valid JSON.", null).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(output, null, "cancelled", "Plugin operation was cancelled.", null).ConfigureAwait(false);
            return 130;
        }
        catch
        {
            // Deliberately avoid exception messages: HttpClient exceptions can include secret-bearing URLs.
            await WriteErrorAsync(output, null, "plugin_failure", "Plugin operation failed.", null).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WriteErrorAsync(TextWriter output, string? id, string code, string message, int? statusCode)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "error",
            id,
            error = new { code, message, status_code = statusCode },
        })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 4_096));
        var character = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (character[0] == '\n') return builder.ToString().TrimEnd('\r');
            if (builder.Length >= maximumCharacters) throw new InvalidDataException("Protocol line is too long.");
            builder.Append(character[0]);
        }
    }
}
