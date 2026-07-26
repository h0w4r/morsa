using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Workspace;

namespace Morsa.UnitTests;

public sealed class ArtifactStorageSecurityTests
{
    [Fact]
    public async Task StoreAsync_MaliciousSuggestedName_CannotEscapeContentAddressedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-storage", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ContentAddressableArtifactStorage(new WorkspaceContext(root), new MagicByteArtifactInspector());
            await using var content = new MemoryStream("safe payload"u8.ToArray());

            var result = await storage.StoreAsync(content, "../../escaped.txt", 1024, CancellationToken.None);

            Assert.StartsWith(
                Path.GetFullPath(Path.Combine(root, "artifacts", "by-hash")),
                Path.GetFullPath(result.Path),
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StoreAsync_OverBudget_RemovesQuarantinePartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-storage", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ContentAddressableArtifactStorage(new WorkspaceContext(root), new MagicByteArtifactInspector());
            await using var content = new MemoryStream(new byte[2048]);

            await Assert.ThrowsAsync<InvalidDataException>(() => storage.StoreAsync(content, "large.bin", 1024, CancellationToken.None));

            var quarantine = Path.Combine(root, "artifacts", "quarantine");
            Assert.Empty(Directory.EnumerateFiles(quarantine));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StoreAsync_SameContent_IsIdempotentByHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "morsa-storage", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new ContentAddressableArtifactStorage(new WorkspaceContext(root), new MagicByteArtifactInspector());
            await using var first = new MemoryStream("same"u8.ToArray());
            await using var second = new MemoryStream("same"u8.ToArray());

            var storedFirst = await storage.StoreAsync(first, "first.txt", 1024, CancellationToken.None);
            var storedSecond = await storage.StoreAsync(second, "second.txt", 1024, CancellationToken.None);

            Assert.Equal(storedFirst.Sha256, storedSecond.Sha256);
            Assert.Equal(storedFirst.Path, storedSecond.Path);
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(storedFirst.Path)!));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
