using System.IO.Compression;
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Artifacts;

/// <summary>Identifies supported formats from content before consulting extensions.</summary>
public sealed class MagicByteArtifactInspector : IArtifactInspector
{
    private static readonly byte[] OleSignature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];
    private static readonly byte[] WordPerfectSignature = [0xff, 0x57, 0x50, 0x43];
    private static readonly byte[] InDesignSignature =
        [0x06, 0x06, 0xed, 0xf5, 0xd8, 0x1d, 0x46, 0xe5, 0xbd, 0x31, 0xef, 0xe7, 0xfe, 0x74, 0xb7, 0x1d];

    public async Task<(ArtifactKind Kind, string? MimeType)> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = new byte[512];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        var bytes = header.AsSpan(0, read);

        if (bytes.StartsWith("%PDF-"u8))
        {
            return (ArtifactKind.Pdf, "application/pdf");
        }

        if (bytes.StartsWith(OleSignature))
        {
            return (ArtifactKind.OleCompound, "application/x-ole-storage");
        }

        if (bytes.StartsWith(WordPerfectSignature))
        {
            return (ArtifactKind.WordPerfect, "application/vnd.wordperfect");
        }

        if (bytes.StartsWith(InDesignSignature))
        {
            return (ArtifactKind.InDesign, "application/x-indesign");
        }

        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return (ArtifactKind.Image, "image/png");
        }

        if (bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }))
        {
            return (ArtifactKind.Image, "image/jpeg");
        }

        if (bytes.StartsWith("II*\0"u8) || bytes.StartsWith("MM\0*"u8))
        {
            return (ArtifactKind.Image, "image/tiff");
        }

        if (bytes.StartsWith("PK\x03\x04"u8) || bytes.StartsWith("PK\x05\x06"u8))
        {
            return InspectZip(path);
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return (ArtifactKind.Svg, "image/svg+xml");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".rdp" => (ArtifactKind.Rdp, "application/x-rdp"),
            ".ica" => (ArtifactKind.Ica, "application/x-ica"),
            ".indd" => (ArtifactKind.InDesign, "application/x-indesign"),
            ".wpd" => (ArtifactKind.WordPerfect, "application/vnd.wordperfect"),
            _ when LooksLikeText(bytes) => (ArtifactKind.Text, "text/plain"),
            _ => (ArtifactKind.Unknown, "application/octet-stream"),
        };
    }

    private static (ArtifactKind Kind, string? MimeType) InspectZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Take(2_000).Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("[Content_Types].xml"))
            {
                return (ArtifactKind.OpenXml, "application/vnd.openxmlformats-officedocument");
            }

            if (names.Contains("META-INF/manifest.xml") || names.Contains("meta.xml"))
            {
                return (ArtifactKind.OpenDocument, "application/vnd.oasis.opendocument");
            }

            return (ArtifactKind.Zip, "application/zip");
        }
        catch (InvalidDataException)
        {
            return (ArtifactKind.Unknown, "application/octet-stream");
        }
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        var controls = 0;
        foreach (var value in bytes)
        {
            if (value == 0)
            {
                return false;
            }

            if (value < 0x09 || value is > 0x0d and < 0x20)
            {
                controls++;
            }
        }

        return controls <= bytes.Length / 20;
    }
}
