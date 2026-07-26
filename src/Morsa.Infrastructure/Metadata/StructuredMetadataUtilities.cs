// SPDX-License-Identifier: GPL-3.0-or-later
// Selective clean port of FOCA XMPExtractor.cs at commit 754453ad7f9579a6021c484d5014a3cd12fd0e35.
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MetadataExtractor;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;

namespace Morsa.Infrastructure.Metadata;

/// <summary>
/// Bounded helpers shared by container parsers. They intentionally parse metadata only and never
/// render a document, follow a link, resolve an entity or execute embedded content.
/// </summary>
internal static partial class StructuredMetadataUtilities
{
    private const int MaximumXmpPackets = 64;
    private const int MaximumXmpCharacters = 8 * 1024 * 1024;
    private const int MaximumXmpObservations = 20_000;
    private const int MaximumEmbeddedImages = 32;
    private const int MaximumEmbeddedImageBytes = 32 * 1024 * 1024;

    /// <summary>Finds complete XMP/RDF packets in a bounded buffer and parses them with DTDs disabled.</summary>
    public static void AddXmpPackets(
        ReadOnlySpan<byte> bytes,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        // XMP markup is ASCII-compatible even when its text values use UTF-8.
        var text = Encoding.UTF8.GetString(bytes);
        var cursor = 0;
        var packetCount = 0;
        while (cursor < text.Length && packetCount < MaximumXmpPackets)
        {
            var xmpStart = text.IndexOf("<x:xmpmeta", cursor, StringComparison.OrdinalIgnoreCase);
            var rdfStart = text.IndexOf("<rdf:RDF", cursor, StringComparison.OrdinalIgnoreCase);
            var start = SelectFirst(xmpStart, rdfStart);
            if (start < 0)
            {
                break;
            }

            var xmpPacket = xmpStart >= 0 && xmpStart == start;
            var closing = xmpPacket ? "</x:xmpmeta>" : "</rdf:RDF>";
            var close = text.IndexOf(closing, start, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                diagnostics.Add(new("xmp.truncated", $"Incomplete XMP packet at {location}.", false));
                break;
            }

            var end = checked(close + closing.Length);
            var length = end - start;
            if (length <= MaximumXmpCharacters)
            {
                AddXmpDocument(
                    text.Substring(start, length),
                    artifactId,
                    extractorId,
                    extractorVersion,
                    $"{location}/xmp:{packetCount}",
                    observations,
                    diagnostics);
            }
            else
            {
                diagnostics.Add(new("xmp.size_budget", $"XMP packet exceeds the character budget at {location}.", true));
            }

            packetCount++;
            cursor = end;
        }

        if (packetCount == MaximumXmpPackets && cursor < text.Length)
        {
            diagnostics.Add(new("xmp.packet_budget", $"XMP packet budget reached at {location}.", true));
        }
    }

    /// <summary>
    /// Recovers isolated, namespace-prefixed XMP properties when a producer embedded only an XML
    /// fragment. This is deliberately limited to leaf text/attributes and never treats it as XML.
    /// </summary>
    public static void AddLooseXmpProperties(
        ReadOnlySpan<byte> bytes,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations)
    {
        var text = Encoding.UTF8.GetString(bytes);
        foreach (Match match in LooseXmpElementRegex().Matches(text).Cast<Match>().Take(10_000))
        {
            Emit(match.Groups["name"].Value, match.Groups["value"].Value, "element");
        }

        foreach (Match match in LooseXmpAttributeRegex().Matches(text).Cast<Match>().Take(10_000))
        {
            Emit(match.Groups["name"].Value, match.Groups["value"].Value, "attribute");
        }

        void Emit(string property, string rawValue, string source)
        {
            var value = System.Net.WebUtility.HtmlDecode(rawValue).Trim();
            if (value.Length is 0 or > 16_384) return;
            var canonical = MapXmpCategory(property, null);
            observations.Add(MetadataUtilities.Observation(
                artifactId,
                canonical,
                value,
                extractorId,
                extractorVersion,
                $"{location}/loose-xmp/{source}:{property}",
                0.78));
            observations.Add(MetadataUtilities.Observation(
                artifactId,
                $"xmp.{property.ToLowerInvariant()}",
                value,
                extractorId,
                extractorVersion,
                $"{location}/loose-xmp/{source}:{property}",
                0.75));
        }
    }

    /// <summary>Extracts stable observations from XMP attributes, leaf nodes and RDF containers.</summary>
    private static void AddXmpDocument(
        string xml,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmpCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var elements = document.Descendants().Take(MaximumXmpObservations + 1).ToArray();
            if (elements.Length > MaximumXmpObservations)
            {
                diagnostics.Add(new("xmp.element_budget", $"XMP element budget reached at {location}.", true));
                return;
            }

            if (elements.Any(element => element.Ancestors().Take(65).Count() > 64))
            {
                diagnostics.Add(new("xmp.depth_budget", $"XMP nesting budget exceeded at {location}.", true));
                return;
            }

            var emitted = 0;
            foreach (var element in elements)
            {
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration || string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        continue;
                    }

                    var category = MapXmpCategory(attribute.Name.LocalName, FindPropertyAncestor(element));
                    Emit(category, attribute.Value, $"{location}/@{attribute.Name.LocalName}");
                }

                if (element.HasElements)
                {
                    continue;
                }

                var value = element.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var propertyName = IsRdfContainerItem(element.Name.LocalName)
                    ? FindPropertyAncestor(element)
                    : element.Name.LocalName;
                Emit(MapXmpCategory(propertyName, null), value, $"{location}/{propertyName}");
            }

            void Emit(string category, string value, string valueLocation)
            {
                if (emitted >= MaximumXmpObservations)
                {
                    return;
                }

                var clean = System.Net.WebUtility.HtmlDecode(value).Trim();
                if (clean.Length is 0 or > 16_384)
                {
                    return;
                }

                observations.Add(MetadataUtilities.Observation(
                    artifactId,
                    category,
                    clean,
                    extractorId,
                    extractorVersion,
                    valueLocation,
                    0.92));
                emitted++;
            }
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            diagnostics.Add(new("xmp.invalid", $"Invalid XMP at {location}: {exception.Message}", false));
        }
    }

    /// <summary>Extracts EXIF/IPTC/XMP observations from an embedded image without decoding pixels.</summary>
    public static void AddEmbeddedImageMetadata(
        ReadOnlyMemory<byte> image,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        if (image.Length is 0 or > MaximumEmbeddedImageBytes)
        {
            diagnostics.Add(new("embedded_image.size_budget", $"Embedded image rejected by size budget at {location}.", true));
            return;
        }

        try
        {
            observations.Add(MetadataUtilities.Observation(
                artifactId,
                "embedded_image",
                $"{GuessImageFormat(image.Span)}:{image.Length}",
                extractorId,
                extractorVersion,
                location,
                1.0));

            using var stream = new MemoryStream(image.ToArray(), writable: false);
            foreach (var directory in ImageMetadataReader.ReadMetadata(stream))
            {
                foreach (var tag in directory.Tags.Take(10_000))
                {
                    if (string.IsNullOrWhiteSpace(tag.Description))
                    {
                        continue;
                    }

                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        MapImageCategory(tag.Name),
                        tag.Description,
                        extractorId,
                        extractorVersion,
                        $"{location}/{directory.Name}/{tag.Name}",
                        0.9));
                }
            }
        }
        catch (Exception exception) when (exception is ImageProcessingException or IOException or ArgumentException)
        {
            diagnostics.Add(new("embedded_image.invalid", $"Embedded image metadata could not be parsed at {location}: {exception.Message}", false));
        }
    }

    /// <summary>Finds complete JPEG and PNG payloads inside a bounded legacy stream.</summary>
    public static void FindAndAddEmbeddedImages(
        ReadOnlyMemory<byte> container,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations,
        ICollection<ExtractionDiagnostic> diagnostics)
    {
        var span = container.Span;
        var cursor = 0;
        var imageIndex = 0;
        while (cursor < span.Length && imageIndex < MaximumEmbeddedImages)
        {
            var jpeg = span[cursor..].IndexOf(new byte[] { 0xff, 0xd8, 0xff });
            var png = span[cursor..].IndexOf(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
            var relativeStart = SelectFirst(jpeg, png);
            if (relativeStart < 0)
            {
                break;
            }

            var start = cursor + relativeStart;
            int end;
            if (jpeg >= 0 && jpeg == relativeStart)
            {
                var relativeEnd = span[(start + 3)..].IndexOf(new byte[] { 0xff, 0xd9 });
                if (relativeEnd < 0)
                {
                    break;
                }

                end = start + 3 + relativeEnd + 2;
            }
            else
            {
                var relativeEnd = span[(start + 8)..].IndexOf(new byte[] { 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82 });
                if (relativeEnd < 0)
                {
                    break;
                }

                end = start + 8 + relativeEnd + 8;
            }

            var length = end - start;
            if (length <= MaximumEmbeddedImageBytes)
            {
                AddEmbeddedImageMetadata(
                    container.Slice(start, length),
                    artifactId,
                    extractorId,
                    extractorVersion,
                    $"{location}/image:{imageIndex}",
                    observations,
                    diagnostics);
            }

            imageIndex++;
            cursor = end;
        }

        if (imageIndex == MaximumEmbeddedImages && cursor < span.Length)
        {
            diagnostics.Add(new("embedded_image.count_budget", $"Embedded image count budget reached at {location}.", true));
        }
    }

    /// <summary>Adds paths, URLs, e-mails, printers and applications from bounded legacy strings.</summary>
    public static void AddBinaryIndicators(
        ReadOnlySpan<byte> bytes,
        Guid artifactId,
        string extractorId,
        string extractorVersion,
        string location,
        ICollection<MetadataObservation> observations)
    {
        foreach (var text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes) })
        {
            AddMatches(EmailRegex(), "email", 0.75);
            AddMatches(UrlRegex(), "url", 0.75);
            AddMatches(PathRegex(), "path", 0.72);
            AddMatches(PrinterRegex(), "printer", 0.7);
            AddMatches(ApplicationRegex(), "application", 0.68);

            void AddMatches(Regex regex, string category, double confidence)
            {
                foreach (Match match in regex.Matches(text).Cast<Match>().Take(500))
                {
                    var value = match.Groups["value"].Success ? match.Groups["value"].Value : match.Value;
                    observations.Add(MetadataUtilities.Observation(
                        artifactId,
                        category,
                        value.Trim('\0', ' ', '\r', '\n', '\t'),
                        extractorId,
                        extractorVersion,
                        location,
                        confidence));
                }
            }
        }
    }

    private static int SelectFirst(int first, int second) =>
        first < 0 ? second : second < 0 ? first : Math.Min(first, second);

    private static bool IsRdfContainerItem(string localName) =>
        localName.Equals("li", StringComparison.OrdinalIgnoreCase);

    private static string FindPropertyAncestor(XElement element)
    {
        foreach (var ancestor in element.Ancestors())
        {
            var local = ancestor.Name.LocalName;
            if (!local.Equals("RDF", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("Description", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("Seq", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("Bag", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("Alt", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("li", StringComparison.OrdinalIgnoreCase) &&
                !local.Equals("xmpmeta", StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }
        }

        return element.Name.LocalName;
    }

    private static string MapXmpCategory(string localName, string? ancestorProperty)
    {
        var property = localName.Equals("li", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ancestorProperty)
            ? ancestorProperty
            : localName;
        return property.ToLowerInvariant() switch
        {
            "creator" or "author" => "author",
            "creatortool" or "producer" or "softwareagent" => "application",
            "createdate" or "creationdate" or "modifydate" or "moddate" or "metadatadate" or "when" => "date",
            "title" => "title",
            "description" => "comments",
            "subject" => "subject",
            "lasturl" or "manager" => "path",
            "documentancestors" => "document_ancestor",
            "action" => "history.action",
            "changed" => "history.changed",
            "instanceid" or "originaldocumentid" or "documentid" => "document_id",
            "nickname" => "embedded_object",
            _ => $"xmp.{property.ToLowerInvariant()}",
        };
    }

    private static string MapImageCategory(string tagName)
    {
        if (tagName.Contains("GPS", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Latitude", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Longitude", StringComparison.OrdinalIgnoreCase)) return "gps";
        if (tagName.Contains("Date", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Time", StringComparison.OrdinalIgnoreCase)) return "date";
        if (tagName.Contains("Software", StringComparison.OrdinalIgnoreCase)) return "application";
        if (tagName.Contains("Artist", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Creator", StringComparison.OrdinalIgnoreCase) ||
            tagName.Contains("Author", StringComparison.OrdinalIgnoreCase)) return "author";
        if (tagName.Contains("Host Computer", StringComparison.OrdinalIgnoreCase)) return "hostname";
        if (tagName.Contains("Operating System", StringComparison.OrdinalIgnoreCase)) return "operating_system";
        if (tagName.Contains("Model", StringComparison.OrdinalIgnoreCase)) return "device_model";
        return $"image.{tagName.ToLowerInvariant().Replace(' ', '_')}";
    }

    private static string GuessImageFormat(ReadOnlySpan<byte> image) =>
        image.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? "jpeg" :
        image.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }) ? "png" : "image";

    [GeneratedRegex(@"(?<value>[\w.+-]+@[\w.-]+\.[A-Za-z]{2,})", RegexOptions.NonBacktracking)]
    private static partial Regex EmailRegex();

    [GeneratedRegex("""(?<value>(?:https?|ftp|ldap)://[^\x00-\x20<>"']{3,2048})""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<value>(?:[A-Za-z]:\\|\\\\)[^\x00-\x1f<>:\""|?*]{2,1024})", RegexOptions.NonBacktracking)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"(?<value>(?:winspool|printer|printto)[\x00:= ]{0,4}[^\x00\r\n]{3,256})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex PrinterRegex();

    [GeneratedRegex(@"(?<value>(?:Adobe|Acrobat|InDesign|LibreOffice|OpenOffice|Microsoft (?:Word|Excel|PowerPoint)|Corel WordPerfect)[^\x00\r\n]{0,128})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ApplicationRegex();

    [GeneratedRegex(@"<(?:dc|xmp|xap|pdf|photoshop):(?<name>[A-Za-z][A-Za-z0-9_-]*)[^>]*>(?<value>[^<]+)<", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex LooseXmpElementRegex();

    [GeneratedRegex("""(?:dc|xmp|xap|pdf|photoshop):(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*=\s*["'](?<value>[^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex LooseXmpAttributeRegex();
}
