using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Safely extracts attributes, comments and links from SVG XML.</summary>
public sealed class SvgMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.svg";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Svg];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var diagnostics = new List<ExtractionDiagnostic>();
        try
        {
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Min(options.MaxBytes, 16 * 1024 * 1024),
            };
            await using var stream = File.OpenRead(artifact.Path);
            using var reader = XmlReader.Create(stream, settings);
            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (observations.Count >= 100_000)
                {
                    diagnostics.Add(new("svg.observation_budget", "SVG metadata observation budget reached.", true));
                    break;
                }
                if (reader.NodeType == XmlNodeType.Comment && !string.IsNullOrWhiteSpace(reader.Value))
                {
                    observations.Add(MetadataUtilities.Observation(
                        artifact.ArtifactId, "comments", reader.Value, Id, Version, "svg/comment"));
                }

                if (reader.NodeType == XmlNodeType.Element && reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        if ((reader.LocalName is "href" or "about" or "resource") &&
                            !string.IsNullOrWhiteSpace(reader.Value))
                        {
                            observations.Add(MetadataUtilities.Observation(
                                artifact.ArtifactId,
                                Uri.TryCreate(reader.Value, UriKind.Absolute, out _) ? "url" : "external_relationship",
                                reader.Value,
                                Id,
                                Version,
                                $"svg/@{reader.LocalName}"));
                        }
                    }
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "creator" or "title" or "description")
                {
                    var localName = reader.LocalName;
                    var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        observations.Add(MetadataUtilities.Observation(
                            artifact.ArtifactId,
                            localName == "creator" ? "author" : localName == "description" ? "comments" : "title",
                            value,
                            Id,
                            Version,
                            $"svg/{localName}"));
                    }
                    continue;
                }

                await reader.ReadAsync().ConfigureAwait(false);
            }
        }
        catch (XmlException exception)
        {
            diagnostics.Add(new("svg.invalid", exception.Message, true));
        }

        return new ExtractionResult(observations, [], diagnostics);
    }
}

/// <summary>Parses RDP, ICA and generic key-value text without executing content.</summary>
public sealed partial class TextMetadataExtractor : IArtifactExtractor
{
    public string Id => "builtin.text";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } =
        [ArtifactKind.Text, ArtifactKind.Rdp, ArtifactKind.Ica];

    public async ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var observations = new List<MetadataObservation>();
        var info = new FileInfo(artifact.Path);
        if (info.Length > Math.Min(options.MaxBytes, 16 * 1024 * 1024))
        {
            return new ExtractionResult([], [], [new("text.size_budget", "Text file exceeds parser budget.", true)]);
        }

        var lines = await File.ReadAllLinesAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOfAny(['=', ':']);
            if (separator > 0 && separator < line.Length - 1)
            {
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var category = MapKey(key);
                    observations.Add(MetadataUtilities.Observation(
                        artifact.ArtifactId,
                        category,
                        category == "credential_indicator" ? "[redacted]" : value,
                        Id,
                        Version,
                        $"line:{index + 1}"));
                }
            }

            foreach (Match match in EmailRegex().Matches(line))
            {
                observations.Add(MetadataUtilities.Observation(
                    artifact.ArtifactId, "email", match.Value, Id, Version, $"line:{index + 1}", 0.9));
            }
            if (observations.Count >= 100_000)
                return new ExtractionResult(observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], [new("text.observation_budget", "Text metadata observation budget reached.", true)]);
        }

        return new ExtractionResult(observations.DistinctBy(item => (item.Category, item.NormalizedValue)).ToArray(), [], []);
    }

    private static string MapKey(string key)
    {
        var normalized = key.ToLowerInvariant();
        return normalized switch
        {
            "full address" or "address" or "server" or "tcpbrowseraddress" => "server",
            "gatewayhostname" or "sslproxyhost" or "httpbrowseraddress" => "server",
            "username" or "user name" or "usernamehint" => "username",
            "domain" => "domain",
            "clientname" or "hostname" => "hostname",
            "password" or "password 51" or "passwordscrambled" or "clearpassword" => "credential_indicator",
            "alternate shell" or "remoteapplicationprogram" or "initialprogram" => "application",
            "clientdirectory" or "workdirectory" => "path",
            _ when normalized.Contains("printer", StringComparison.Ordinal) => "printer",
            _ => $"config.{normalized.Replace(' ', '_')}",
        };
    }

    [GeneratedRegex(@"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])")]
    private static partial Regex EmailRegex();
}
