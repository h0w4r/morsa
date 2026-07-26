using Morsa.Application.Abstractions;

namespace Morsa.PluginSdk;

/// <summary>Versioned manifest declared by every in-process Morsa plugin.</summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Author,
    string ApiVersion,
    IReadOnlyCollection<string> Permissions);

/// <summary>Registry deliberately exposes capabilities instead of a service-provider escape hatch.</summary>
public interface IMorsaPluginRegistry
{
    void RegisterExtractor(IArtifactExtractor extractor);

    void RegisterSearchProvider(ISearchProvider provider);
}

/// <summary>Stable entry point for a managed Morsa plugin.</summary>
public interface IMorsaPlugin
{
    PluginManifest Manifest { get; }

    Task RegisterAsync(IMorsaPluginRegistry registry, CancellationToken cancellationToken);
}

