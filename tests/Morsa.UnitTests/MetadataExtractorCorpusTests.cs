using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

namespace Morsa.UnitTests;

public sealed class MetadataExtractorCorpusTests
{
    [Fact]
    public async Task ZipXmlExtractor_SyntheticOoxml_ExtractsCoreAndApplicationProperties()
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateZip(
            "sample.docx",
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"urn:cp\" xmlns:dc=\"urn:dc\"><dc:creator>Chris Tester</dc:creator>\n<dc:title>Morsa</dc:title></cp:coreProperties>"),
            ("docProps/app.xml", "<Properties><Application>LibreOffice</Application>\n<Company>Morsa Labs</Company></Properties>"));

        var result = await ExtractAsync(new ZipXmlMetadataExtractor(), path, ArtifactKind.OpenXml);

        Assert.Contains(result.Observations, item => item.Category == "author" && item.OriginalValue == "Chris Tester");
        Assert.Contains(result.Observations, item => item.Category == "application" && item.OriginalValue == "LibreOffice");
        Assert.Contains(result.Observations, item => item.Category == "company" && item.OriginalValue == "Morsa Labs");
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ZipXmlExtractor_SyntheticOdf_ExtractsGeneratorAndEditingCycles()
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateZip(
            "sample.odt",
            ("META-INF/manifest.xml", "<manifest/>"),
            ("meta.xml", "<office:document-meta xmlns:office=\"urn:office\" xmlns:meta=\"urn:meta\"><meta:generator>OnlyOffice</meta:generator>\n<meta:editing-cycles>7</meta:editing-cycles></office:document-meta>"));

        var result = await ExtractAsync(new ZipXmlMetadataExtractor(), path, ArtifactKind.OpenDocument);

        Assert.Contains(result.Observations, item => item.Category == "application" && item.OriginalValue == "OnlyOffice");
        Assert.Contains(result.Observations, item => item.Category == "revision" && item.OriginalValue == "7");
    }

    [Fact]
    public async Task ZipXmlExtractor_PathTraversalEntry_ReportsDiagnosticWithoutWritingOutsideCorpus()
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateZip("unsafe.zip", ("../docProps/core.xml", "<creator>attacker</creator>"));

        var result = await ExtractAsync(new ZipXmlMetadataExtractor(), path, ArtifactKind.Zip);

        Assert.Contains(result.Diagnostics, item => item.Code == "zip.path_traversal" && item.IsError);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task PdfExtractor_SyntheticInfoAndXmp_ExtractsTraceableValues()
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateText("sample.pdf", "%PDF-1.7\n/Author (Chris Tester) /Producer (Morsa PDF)\n<dc:title>Morsa report</dc:title>\n%%EOF");

        var result = await ExtractAsync(new PdfMetadataExtractor(), path, ArtifactKind.Pdf);

        Assert.Contains(result.Observations, item => item.Category == "author" && item.Location == "pdf/info/Author");
        Assert.Contains(result.Observations, item => item.Category == "application" && item.OriginalValue == "Morsa PDF");
        Assert.Contains(result.Observations, item => item.Category == "xmp.title" && item.OriginalValue == "Morsa report");
    }

    [Fact]
    public async Task SvgExtractor_CommentsAndLinks_ExtractsBothWithoutResolvingEntities()
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateText("sample.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><!--designer@example.com--><image xlink:href=\"https://example.test/logo.png\"/></svg>");

        var result = await ExtractAsync(new SvgMetadataExtractor(), path, ArtifactKind.Svg);

        Assert.Contains(result.Observations, item => item.Category == "comments" && item.OriginalValue == "designer@example.com");
        Assert.Contains(result.Observations, item => item.Category == "url" && item.OriginalValue == "https://example.test/logo.png");
    }

    [Theory]
    [InlineData("connection.rdp", "full address:s:rdp.example.test\nusername:s:EXAMPLE\\chris", ArtifactKind.Rdp, "server", "s:rdp.example.test")]
    [InlineData("connection.ica", "Address=ica.example.test\nUsername=chris@example.test", ArtifactKind.Ica, "server", "ica.example.test")]
    public async Task TextExtractor_RemoteDesktopFormats_NormalizesKnownKeys(
        string name,
        string content,
        ArtifactKind kind,
        string category,
        string expected)
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateText(name, content);

        var result = await ExtractAsync(new TextMetadataExtractor(), path, kind);

        Assert.Contains(result.Observations, item => item.Category == category && item.OriginalValue == expected);
    }

    [Fact]
    public async Task BinaryFallback_UnknownContent_ExtractsEmailUncAndUrl()
    {
        using var corpus = new SyntheticCorpus();
        var bytes = Encoding.Latin1.GetBytes("\0\x01owner@example.test\0\\\\fileserver\\finance\\budget.xlsx\0https://portal.example.test/docs\0");
        var path = corpus.CreateBinary("legacy.bin", bytes);

        var result = await ExtractAsync(new BinaryStringsMetadataExtractor(), path, ArtifactKind.Unknown);

        Assert.Contains(result.Observations, item => item.Category == "email" && item.OriginalValue == "owner@example.test");
        Assert.Contains(result.Observations, item => item.Category == "unc_path");
        Assert.Contains(result.Observations, item => item.Category == "url");
    }

    [Fact]
    public async Task ImageExtractor_MinimalPng_ParsesContainerWithoutFailure()
    {
        using var corpus = new SyntheticCorpus();
        // Valid 1x1 PNG with a deterministic software text chunk.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var path = corpus.CreateBinary("pixel.png", png);

        var result = await ExtractAsync(new ImageMetadataExtractor(), path, ArtifactKind.Image);

        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
        Assert.NotEmpty(result.Observations);
    }

    [Theory]
    [InlineData("%PDF-1.4", "fake.txt", ArtifactKind.Pdf)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", "fake.bin", ArtifactKind.Svg)]
    [InlineData("full address:s:host", "sample.rdp", ArtifactKind.Rdp)]
    public async Task MagicInspector_ContentAndExtensions_ClassifySyntheticCorpus(string content, string name, ArtifactKind expected)
    {
        using var corpus = new SyntheticCorpus();
        var path = corpus.CreateText(name, content);

        var result = await new MagicByteArtifactInspector().InspectAsync(path, CancellationToken.None);

        Assert.Equal(expected, result.Kind);
    }

    private static ValueTask<ExtractionResult> ExtractAsync(IArtifactExtractor extractor, string path, ArtifactKind kind) =>
        extractor.ExtractAsync(
            new ArtifactContext(Guid.NewGuid(), path, "synthetic-sha256", kind, null),
            new ExtractionOptions(),
            CancellationToken.None);
}
