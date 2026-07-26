using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

namespace Morsa.ParserHost;

/// <summary>JSONL worker that parses one hostile artifact per request.</summary>
public static class Program
{
    public static async Task<int> Main()
    {
        var inspector = new MagicByteArtifactInspector();
        var registry = new ArtifactExtractorRegistry();
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            try
            {
                var request = JsonSerializer.Deserialize<ParserRequest>(line) ??
                              throw new InvalidDataException("Invalid parser request.");
                var fullPath = Path.GetFullPath(request.Path);
                var inspected = await inspector.InspectAsync(fullPath, CancellationToken.None).ConfigureAwait(false);
                var extractor = registry.Select(inspected.Kind) ??
                                throw new NotSupportedException($"No extractor supports {inspected.Kind}.");
                var result = await extractor.ExtractAsync(
                    new ArtifactContext(request.ArtifactId, fullPath, request.Sha256, inspected.Kind, inspected.MimeType),
                    new ExtractionOptions(),
                    CancellationToken.None).ConfigureAwait(false);
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { type = "result", request.Id, result }))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "error",
                    code = "parser.failed",
                    message = exception.Message,
                })).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private sealed record ParserRequest(string Id, Guid ArtifactId, string Path, string Sha256);
}

