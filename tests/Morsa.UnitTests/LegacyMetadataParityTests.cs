using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

namespace Morsa.UnitTests;

public sealed class LegacyMetadataParityTests
{
    [Fact]
    public async Task InDesignExtractor_XmpPathsAndEmbeddedExif_PreservesTraceableMetadata()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreateInDesign();

        var result = await ExtractAsync(new InDesignMetadataExtractor(), path, ArtifactKind.InDesign);

        AssertObservation(result, "author", "InDesign Author");
        AssertObservation(result, "title", "Morsa Layout");
        AssertObservation(result, "application", "Adobe InDesign 20");
        Assert.Contains(result.Observations, item => item.Category == "path" && item.OriginalValue.Contains("layout.user", StringComparison.Ordinal));
        Assert.Contains(result.Observations, item => item.Category == "embedded_image");
        AssertObservation(result, "author", "Embedded InDesign Artist");
        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
    }

    [Fact]
    public async Task WordPerfectExtractor_MarkedRecords_ExtractsPathsUsersPrintersAndApplications()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreateWordPerfect();

        var result = await ExtractAsync(new WordPerfectMetadataExtractor(), path, ArtifactKind.WordPerfect);

        Assert.Contains(result.Observations, item => item.Category == "path" && item.OriginalValue.Contains("contract.wpd", StringComparison.Ordinal));
        AssertObservation(result, "username", "wp.user");
        Assert.Contains(result.Observations, item => item.Category == "printer" && item.OriginalValue.Contains("LaserJet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Observations, item => item.Category == "application" && item.OriginalValue.Contains("Adobe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
    }

    [Fact]
    public async Task WordPerfectExtractor_InvalidSignature_FailsClosedWithDiagnostic()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.Write("spoofed.wpd", "not a wordperfect file"u8.ToArray());

        var result = await ExtractAsync(new WordPerfectMetadataExtractor(), path, ArtifactKind.WordPerfect);

        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, item => item.Code == "wordperfect.invalid_signature" && item.IsError);
    }

    [Fact]
    public async Task OleExtractor_PropertySetsHistoryNativeObjectAndExif_ExceedLegacyFocaCoverage()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreateOleCompound();

        var result = await ExtractAsync(new OleMetadataExtractor(), path, ArtifactKind.OleCompound);

        AssertObservation(result, "title", "Morsa legacy report");
        AssertObservation(result, "author", "OLE Author");
        AssertObservation(result, "last_saved_by", "OLE Last Editor");
        AssertObservation(result, "company", "Morsa Labs");
        AssertObservation(result, "manager", "Morsa Manager");
        AssertObservation(result, "slide_count", "4");
        AssertObservation(result, "operating_system", "Windows XP");
        Assert.Contains(result.Observations, item => item.Category == "template" && item.OriginalValue.EndsWith("normal.dot", StringComparison.Ordinal));
        AssertObservation(result, "history.author", "Historical Author");
        Assert.Contains(result.Observations, item => item.Category == "path" && item.OriginalValue.Contains("previous.doc", StringComparison.Ordinal));
        Assert.Contains(result.Observations, item => item.Category == "embedded_object" && item.OriginalValue == "photo.jpg");
        AssertObservation(result, "author", "Embedded OLE Artist");
        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
    }

    [Fact]
    public async Task OleExtractor_PackedUtf8CustomDictionary_ExtractsAllNamedProperties()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreateOleCompoundWithPackedUtf8CustomDictionary();

        var result = await ExtractAsync(new OleMetadataExtractor(), path, ArtifactKind.OleCompound);

        AssertObservation(result, "company", "Morsa Perú");
        AssertObservation(result, "custom.evidencepath", @"C:\Users\chris.kali\Documents\evidence.doc");
        AssertObservation(result, "manager", "Chris Acceptance Manager");
        AssertObservation(result, "custom.morsacase", "KALI-REAL-DOC");
        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
    }

    [Fact]
    public async Task OleExtractor_TruncatedPropertySet_ReportsPartialFailureWithoutThrowing()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreateMalformedOlePropertySet();

        var result = await ExtractAsync(new OleMetadataExtractor(), path, ArtifactKind.OleCompound);

        Assert.Contains(result.Diagnostics, item => item.Code == "ole.property_set_invalid" && item.IsError);
    }

    [Fact]
    public async Task PdfExtractor_InfoXmpHistoryFlateEmbeddedFileAndExif_AreAllCovered()
    {
        using var corpus = new LegacySyntheticCorpus();
        var path = corpus.CreatePdf();

        var result = await ExtractAsync(new PdfMetadataExtractor(), path, ArtifactKind.Pdf);

        AssertObservation(result, "title", "Morsa Unicode");
        AssertObservation(result, "author", "Chris (PDF) Tester");
        AssertObservation(result, "author", "XMP Author");
        AssertObservation(result, "history.action", "saved");
        AssertObservation(result, "application", "Adobe InDesign");
        AssertObservation(result, "document_ancestor", "uuid:ancestor-1");
        Assert.Contains(result.Observations, item => item.Category == "embedded_object" && item.OriginalValue.Contains("evidence", StringComparison.Ordinal));
        Assert.Contains(result.Observations, item => item.Category == "path" && item.OriginalValue.Contains("pdf.user", StringComparison.Ordinal));
        AssertObservation(result, "email", "owner@example.test");
        Assert.Contains(result.Observations, item => item.Category == "embedded_image");
        AssertObservation(result, "author", "Embedded PDF Artist");
        Assert.DoesNotContain(result.Diagnostics, item => item.IsError);
    }

    [Fact]
    public void Registry_SelectsDedicatedLegacyExtractorsInsteadOfGenericFallback()
    {
        var registry = new ArtifactExtractorRegistry();

        Assert.IsType<InDesignMetadataExtractor>(registry.Select(ArtifactKind.InDesign));
        Assert.IsType<WordPerfectMetadataExtractor>(registry.Select(ArtifactKind.WordPerfect));
        Assert.IsType<OleMetadataExtractor>(registry.Select(ArtifactKind.OleCompound));
        Assert.IsType<PdfMetadataExtractor>(registry.Select(ArtifactKind.Pdf));
        Assert.IsType<BinaryStringsMetadataExtractor>(registry.Select(ArtifactKind.Unknown));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MagicInspector_LegacySignatures_ClassifyFilesWithoutExtensions(bool wordPerfect)
    {
        using var corpus = new LegacySyntheticCorpus();
        var source = wordPerfect ? corpus.CreateWordPerfect() : corpus.CreateInDesign();
        var content = await File.ReadAllBytesAsync(source);
        var extensionless = corpus.Write(wordPerfect ? "wp-artifact" : "indesign-artifact", content);

        var result = await new MagicByteArtifactInspector().InspectAsync(extensionless, CancellationToken.None);

        Assert.Equal(wordPerfect ? ArtifactKind.WordPerfect : ArtifactKind.InDesign, result.Kind);
    }

    private static void AssertObservation(ExtractionResult result, string category, string expected) =>
        Assert.Contains(result.Observations, item => item.Category == category && item.OriginalValue == expected);

    private static ValueTask<ExtractionResult> ExtractAsync(IArtifactExtractor extractor, string path, ArtifactKind kind) =>
        extractor.ExtractAsync(
            new ArtifactContext(Guid.NewGuid(), path, "synthetic-sha256", kind, null),
            new ExtractionOptions(),
            CancellationToken.None);
}
