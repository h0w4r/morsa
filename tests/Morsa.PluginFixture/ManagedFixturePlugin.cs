using System.Runtime.CompilerServices;
using Morsa.Application.Abstractions;
using Morsa.Domain.Common;
using Morsa.PluginSdk;

namespace Morsa.PluginFixture;

/// <summary>Deterministic managed SDK plugin used to verify out-of-process loading.</summary>
public sealed class ManagedFixturePlugin : IMorsaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "fixture.managed",
        "Managed fixture",
        "1.0.0",
        "Morsa tests",
        "1",
        []);

    public Task RegisterAsync(IMorsaPluginRegistry registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        registry.RegisterExtractor(new FixtureExtractor());
        registry.RegisterSearchProvider(new FixtureSearchProvider());
        return Task.CompletedTask;
    }
}

internal sealed class FixtureExtractor : IArtifactExtractor
{
    public string Id => "fixture.extractor";

    public string Version => "1.0.0";

    public IReadOnlyCollection<ArtifactKind> SupportedKinds { get; } = [ArtifactKind.Text];

    public ValueTask<ExtractionResult> ExtractAsync(
        ArtifactContext artifact,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ExtractionResult([], [], []));
    }
}

internal sealed class FixtureSearchProvider : ISearchProvider
{
    public string Id => "fixture.search";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderHealth(true, "fixture"));

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchQuery query,
        SearchExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Yield once so cancellation and the asynchronous provider contract are exercised.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}
