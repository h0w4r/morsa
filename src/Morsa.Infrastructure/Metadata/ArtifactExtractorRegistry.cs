using Morsa.Application.Abstractions;
using Morsa.Domain.Common;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Immutable registry of built-in metadata extractors.</summary>
public sealed class ArtifactExtractorRegistry : IArtifactExtractorRegistry
{
    private readonly IReadOnlyCollection<IArtifactExtractor> _extractors =
    [
        new ZipXmlMetadataExtractor(),
        new OleMetadataExtractor(),
        new ImageMetadataExtractor(),
        new SvgMetadataExtractor(),
        new PdfMetadataExtractor(),
        new InDesignMetadataExtractor(),
        new WordPerfectMetadataExtractor(),
        new TextMetadataExtractor(),
        new BinaryStringsMetadataExtractor(),
    ];

    public IReadOnlyCollection<IArtifactExtractor> All => _extractors;

    public IArtifactExtractor? Select(ArtifactKind kind) =>
        _extractors.FirstOrDefault(extractor => extractor.SupportedKinds.Contains(kind));
}
