using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Morsa.Application.Abstractions;
using Morsa.Domain.Discovery;

namespace Morsa.Infrastructure.Discovery;

/// <summary>Imports previously collected URLs without treating external data as trusted state.</summary>
public sealed class DiscoveryImportService(IMorsaStore store)
{
    public async Task<int> ImportAsync(
        Guid projectId,
        Guid runId,
        string source,
        string? format,
        int maximum,
        CancellationToken cancellationToken)
    {
        var text = source == "-"
            ? await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
            : await ReadBoundedFileAsync(Path.GetFullPath(source), 64 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var effectiveFormat = (format ?? (source == "-" ? "text" : Path.GetExtension(source).TrimStart('.'))).ToLowerInvariant();
        var values = effectiveFormat switch
        {
            "json" => ParseJson(text),
            "jsonl" or "ndjson" => ParseJsonLines(text),
            "har" => ParseHar(text),
            "csv" => ParseCsv(text),
            "txt" or "text" or "" => ParseText(text),
            _ => throw new InvalidDataException($"Unsupported discovery import format '{effectiveFormat}'."),
        };

        var added = 0;
        var known = await store.DiscoveredResources.Where(item => item.ProjectId == projectId)
            .Select(item => item.CanonicalUrl).ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        foreach (var value in values.Take(Math.Clamp(maximum, 1, 1_000_000)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            var canonical = DiscoveryUtilities.Canonicalize(uri.AbsoluteUri);
            if (!known.Add(canonical)) continue;
            store.Add(new DiscoveredResource
            {
                ProjectId = projectId,
                RunId = runId,
                Url = uri.AbsoluteUri,
                CanonicalUrl = canonical,
                ProviderId = $"import:{effectiveFormat}",
                Query = source == "-" ? "stdin" : Path.GetFileName(source),
                Title = value.Title,
                Snippet = value.Snippet,
            });
            added++;
            if (added % 1_000 == 0) await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    private static IEnumerable<ImportedUrl> ParseText(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).Select(line => new ImportedUrl(line, null, null));

    private static IEnumerable<ImportedUrl> ParseCsv(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count == 0 || fields[0].Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new ImportedUrl(fields[0], fields.ElementAtOrDefault(1), fields.ElementAtOrDefault(2));
        }
    }

    private static IEnumerable<ImportedUrl> ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.GetProperty("results").EnumerateArray();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.String) yield return new ImportedUrl(item.GetString()!, null, null);
            else if (TryParseObject(item, out var value)) yield return value;
        }
    }

    private static IEnumerable<ImportedUrl> ParseJsonLines(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (TryParseObject(document.RootElement, out var value)) yield return value;
        }
    }

    private static IEnumerable<ImportedUrl> ParseHar(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        foreach (var entry in document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray())
        {
            var request = entry.GetProperty("request");
            if (request.TryGetProperty("url", out var url)) yield return new ImportedUrl(url.GetString()!, null, null);
        }
    }

    private static bool TryParseObject(JsonElement item, out ImportedUrl value)
    {
        value = default!;
        if (!item.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return false;
        value = new ImportedUrl(
            url.GetString()!,
            item.TryGetProperty("title", out var title) ? title.GetString() : null,
            item.TryGetProperty("snippet", out var snippet) ? snippet.GetString() : null);
        return true;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(character);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static async Task<string> ReadBoundedFileAsync(string path, long maximum, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Discovery import source does not exist.", path);
        if (info.Length > maximum) throw new InvalidDataException("Discovery import source exceeds 64 MiB.");
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ImportedUrl(string Url, string? Title, string? Snippet);
}
