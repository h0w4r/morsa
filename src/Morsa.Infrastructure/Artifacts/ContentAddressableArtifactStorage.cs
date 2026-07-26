using System.Buffers;
using System.Security.Cryptography;
using Morsa.Application.Abstractions;

namespace Morsa.Infrastructure.Artifacts;

/// <summary>Streams hostile content into quarantine before atomically addressing it by SHA-256.</summary>
public sealed class ContentAddressableArtifactStorage(
    IWorkspaceContext workspace,
    IArtifactInspector inspector) : IArtifactStorage
{
    public async Task<StoredArtifact> StoreAsync(
        Stream source,
        string? suggestedName,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var quarantine = Path.Combine(workspace.ArtifactsPath, "quarantine");
        Directory.CreateDirectory(quarantine);
        var temporaryPath = Path.Combine(quarantine, $"{Guid.NewGuid():N}.part");
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        try
        {
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException($"Artifact exceeds the {maximumBytes} byte budget.");
                }

                hasher.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            // Windows cannot rename an open file; close quarantine before the atomic move.
            await destination.DisposeAsync().ConfigureAwait(false);
            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            var hashDirectory = Path.Combine(workspace.ArtifactsPath, "by-hash", hash[..2]);
            Directory.CreateDirectory(hashDirectory);
            var finalPath = Path.Combine(hashDirectory, hash);

            if (!File.Exists(finalPath))
            {
                File.Move(temporaryPath, finalPath);
            }
            else
            {
                File.Delete(temporaryPath);
            }

            var inspection = await inspector.InspectAsync(finalPath, cancellationToken).ConfigureAwait(false);
            return new StoredArtifact(finalPath, hash, total, inspection.Kind, inspection.MimeType);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
