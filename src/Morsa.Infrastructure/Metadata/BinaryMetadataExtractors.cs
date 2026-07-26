using System.Text;
using System.Text.RegularExpressions;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Fallback for unknown binary formats using bounded printable strings.</summary>
public sealed partial class BinaryStringsMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.binary-strings";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Unknown];

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
        var unicodeText = Encoding.Unicode.GetString(bytes);
        var observations = new List<MetadataObservation>();

        foreach (var candidate in new[] { text, unicodeText })
        {
            foreach (Match match in EmailRegex().Matches(candidate).Cast<Match>().Take(500))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "email", match.Value, Id, Version, "binary/string", 0.7));
            }

            foreach (Match match in UncPathRegex().Matches(candidate).Cast<Match>().Take(500))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "unc_path", match.Value, Id, Version, "binary/string", 0.7));
                var host = match.Value.TrimStart('\\').Split('\\', 2)[0];
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "hostname", host, Id, Version, "binary/unc-host", 0.75));
            }

            foreach (Match match in UrlRegex().Matches(candidate).Cast<Match>().Take(500))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "url", match.Value, Id, Version, "binary/string", 0.7));
            }

            foreach (Match match in WindowsPathRegex().Matches(candidate).Cast<Match>().Take(500))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "path", match.Value, Id, Version, "binary/path", 0.65));
                var user = UserFromPathRegex().Match(match.Value);
                if (user.Success)
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifact.ArtifactId, "username", user.Groups[1].Value, Id, Version, "binary/path-user", 0.7));
                }
            }

            foreach (Match match in XmpRegex().Matches(candidate).Cast<Match>().Take(1_000))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId,
                    MapXmpCategory(match.Groups[1].Value),
                    System.Net.WebUtility.HtmlDecode(match.Groups[2].Value),
                    Id,
                    Version,
                    $"binary/xmp/{match.Groups[1].Value}",
                    0.9));
            }
        }

        return new ExtractionResult(observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], []);
    }

    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\\\\[A-Za-z0-9._-]+\\[^\x00-\x1f\s]{1,512}", RegexOptions.NonBacktracking)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"https?://[^\x00-\x20<>""']{3,2048}", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\x00-\x1f<>:""/\\|?*]+\\){1,20}[^\x00-\x1f<>:""/\\|?*]{0,255}", RegexOptions.NonBacktracking)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"\\Users\\([^\\]{1,128})\\", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UserFromPathRegex();

    [GeneratedRegex(@"<(?:dc|xmp|pdf|photoshop):([A-Za-z][A-Za-z0-9_-]{0,63})[^>]*>([^<]{1,4096})<", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex XmpRegex();

    private static string MapXmpCategory(string property) => property.ToLowerInvariant() switch
    {
        "creator" or "author" => "author",
        "creatortool" or "producer" => "application",
        "createdate" or "modifydate" or "metadatadate" => "date",
        "title" => "title",
        "description" => "comments",
        _ => $"xmp.{property.ToLowerInvariant()}",
    };
}
