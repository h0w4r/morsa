using System.Text;
using System.Text.RegularExpressions;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Extracts bounded PDF Info/XMP strings without rendering active content.</summary>
public sealed partial class PdfMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.pdf";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Pdf];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(artifact.Path, Math.Min(options.MaxBytes, 32 * 1024 * 1024), cancellationToken)
            .ConfigureAwait(false);
        var text = Encoding.Latin1.GetString(bytes);
        var observations = new List<MetadataObservation>();

        foreach (Match match in PdfPropertyRegex().Matches(text))
        {
            var category = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "author" => "author",
                "creator" => "application",
                "producer" => "application",
                "creationdate" or "moddate" => "date",
                "subject" => "subject",
                "title" => "title",
                "keywords" => "keywords",
                _ => "pdf.property",
            };
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId,
                category,
                UnescapePdf(match.Groups[2].Value),
                Id,
                Version,
                $"pdf/info/{match.Groups[1].Value}"));
        }

        foreach (Match match in XmpPropertyRegex().Matches(text))
        {
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId,
                $"xmp.{match.Groups[1].Value.ToLowerInvariant()}",
                match.Groups[2].Value,
                Id,
                Version,
                "pdf/xmp",
                0.9));
        }

        return new ExtractionResult(observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], []);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, long maximum, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > maximum)
        {
            throw new InvalidDataException("PDF exceeds the bounded parser window.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static string UnescapePdf(string value) =>
        value.Replace("\\(", "(", StringComparison.Ordinal)
             .Replace("\\)", ")", StringComparison.Ordinal)
             .Replace("\\\\", "\\", StringComparison.Ordinal);

    [GeneratedRegex(@"/(Author|Creator|Producer|CreationDate|ModDate|Subject|Title|Keywords)\s*\(([^)]{0,4096})\)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PdfPropertyRegex();

    [GeneratedRegex(@"(?:dc|xmp|pdf):([A-Za-z]+)[^>]*>([^<]{1,4096})<", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex XmpPropertyRegex();
}

/// <summary>Fallback for OLE, InDesign and WordPerfect using bounded printable strings.</summary>
public sealed partial class BinaryStringsMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.binary-strings";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } =
        [ArtifactKind.OleCompound, ArtifactKind.InDesign, ArtifactKind.WordPerfect, ArtifactKind.Unknown];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(artifact.Path);
        var maximum = Math.Min(options.MaxBytes, 32 * 1024 * 1024);
        if (info.Length > maximum)
        {
            return new ExtractionResult([], [], [new("binary.size_budget", "Binary exceeds fallback parser budget.", true)]);
        }

        var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        var text = Encoding.Latin1.GetString(bytes);
        var observations = new List<MetadataObservation>();

        foreach (Match match in EmailRegex().Matches(text).Cast<Match>().Take(500))
        {
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId, "email", match.Value, Id, Version, "binary/string", 0.7));
        }

        foreach (Match match in UncPathRegex().Matches(text).Cast<Match>().Take(500))
        {
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId, "unc_path", match.Value, Id, Version, "binary/string", 0.7));
        }

        foreach (Match match in UrlRegex().Matches(text).Cast<Match>().Take(500))
        {
            observations.Add(MetadataUtilities.Observation(
                artifact.ArtifactId, "url", match.Value, Id, Version, "binary/string", 0.7));
        }

        return new ExtractionResult(observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], []);
    }

    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])", RegexOptions.NonBacktracking)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\\\\[A-Za-z0-9._-]+\\[^\x00-\x1f\s]{1,512}", RegexOptions.NonBacktracking)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"https?://[^\x00-\x20<>""']{3,2048}", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UrlRegex();
}
