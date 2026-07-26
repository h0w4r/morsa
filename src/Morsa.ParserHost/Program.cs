using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

namespace Morsa.ParserHost;

/// <summary>JSONL worker that parses one hostile artifact per request.</summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main()
    {
        var inspector = new MagicByteArtifactInspector();
        var registry = new ArtifactExtractorRegistry();
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            try
            {
                var request = JsonSerializer.Deserialize<ParserRequest>(line, JsonOptions) ??
                              throw new InvalidDataException("Invalid parser request.");
                var fullPath = Path.GetFullPath(request.Artifact.Path);
                var inspected = await inspector.InspectAsync(fullPath, CancellationToken.None).ConfigureAwait(false);
                var extractor = registry.Select(inspected.Kind) ??
                                throw new NotSupportedException($"No extractor supports {inspected.Kind}.");
                var result = await extractor.ExtractAsync(
                    request.Artifact with { Path = fullPath, Kind = inspected.Kind, MimeType = inspected.MimeType },
                    request.Options,
                    CancellationToken.None).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { type = "result", request.Id, result }, JsonOptions))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "error",
                    code = "parser.failed",
                    message = exception.Message,
                }, JsonOptions)).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private sealed record ParserRequest(string Id, ArtifactContext Artifact, ExtractionOptions Options);
}
