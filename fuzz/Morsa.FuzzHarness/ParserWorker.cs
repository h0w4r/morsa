using System.Security.Cryptography;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Domain.Artifacts;
using Morsa.Domain.Common;
using Morsa.Infrastructure.Artifacts;
using Morsa.Infrastructure.Metadata;

namespace Morsa.FuzzHarness;

/// <summary>Ejecuta exactamente un input dentro de un proceso desechable.</summary>
internal static class ParserWorker
{
    public static async Task<int> ExecuteAsync(FuzzOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.InputPath);
        var input = new FileInfo(options.InputPath);
        if (!input.Exists || input.Length > options.MaxInputBytes)
        {
            Console.Error.WriteLine("input_rejected: missing or over size budget");
            return ExitCodes.InputRejected;
        }

        using var cancellation = new CancellationTokenSource(options.TimeoutMilliseconds);
        try
        {
            if (options.Target == "magic")
            {
                var inspected = await new MagicByteArtifactInspector()
                    .InspectAsync(input.FullName, cancellation.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schema_version = "morsa-fuzz/1",
                    target = options.Target,
                    kind = inspected.Kind.ToString(),
                    mime_type = inspected.MimeType,
                }));
                return ExitCodes.Success;
            }

            var (extractor, kind) = CreateExtractor(options.Target);
            var hash = await ComputeSha256Async(input.FullName, cancellation.Token).ConfigureAwait(false);
            var artifact = new ArtifactContext(Guid.NewGuid(), input.FullName, hash, kind, null);
            var extractionOptions = new ExtractionOptions(
                MaxBytes: options.MaxInputBytes,
                MaxUncompressedBytes: Math.Min(16L * options.MaxInputBytes, 64L * 1024 * 1024),
                MaxContainerEntries: 2_000,
                MaxDepth: 4,
                Timeout: TimeSpan.FromMilliseconds(options.TimeoutMilliseconds));

            var result = await extractor.ExtractAsync(artifact, extractionOptions, cancellation.Token)
                .ConfigureAwait(false);
            ValidateResult(result);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = "morsa-fuzz/1",
                target = options.Target,
                observations = result.Observations.Count,
                findings = result.Findings.Count,
                diagnostics = result.Diagnostics.Count,
            }));
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("parser_timeout: cooperative timeout elapsed");
            return ExitCodes.Timeout;
        }
        catch (FuzzInvariantException exception)
        {
            Console.Error.WriteLine($"invariant_violation: {exception.Message}");
            return ExitCodes.InvariantViolation;
        }
        catch (Exception exception)
        {
            // El tipo y mensaje bastan para triage; la entrada exacta se conserva fuera del proceso.
            Console.Error.WriteLine($"parser_crash: {exception.GetType().FullName}: {exception.Message}");
            return ExitCodes.ParserCrash;
        }
    }

    private static (IArtifactExtractor Extractor, ArtifactKind Kind) CreateExtractor(string target) => target switch
    {
        "zipxml" => (new ZipXmlMetadataExtractor(), ArtifactKind.Zip),
        "pdf" => (new PdfMetadataExtractor(), ArtifactKind.Pdf),
        "svg" => (new SvgMetadataExtractor(), ArtifactKind.Svg),
        "rdp" => (new TextMetadataExtractor(), ArtifactKind.Rdp),
        "ica" => (new TextMetadataExtractor(), ArtifactKind.Ica),
        "binary" => (new BinaryStringsMetadataExtractor(), ArtifactKind.Unknown),
        _ => throw new ArgumentException($"Unsupported worker target '{target}'."),
    };

    private static void ValidateResult(ExtractionResult result)
    {
        const int maximumItems = 10_000;
        if (result.Observations.Count > maximumItems ||
            result.Findings.Count > maximumItems ||
            result.Diagnostics.Count > maximumItems)
        {
            throw new FuzzInvariantException("Parser returned an unbounded result collection.");
        }

        foreach (var observation in result.Observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Category) ||
                string.IsNullOrWhiteSpace(observation.Extractor) ||
                observation.OriginalValue.Length > 1_000_000 ||
                observation.NormalizedValue.Length > 1_000_000)
            {
                throw new FuzzInvariantException("Parser returned an invalid metadata observation.");
            }
        }

        if (result.Diagnostics.Any(item => string.IsNullOrWhiteSpace(item.Code)))
        {
            throw new FuzzInvariantException("Parser returned a diagnostic without code.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private sealed class FuzzInvariantException(string message) : Exception(message);
}
