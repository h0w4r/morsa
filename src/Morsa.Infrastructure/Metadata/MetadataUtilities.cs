using System.Globalization;
using System.Text;
using Morsa.Domain.Artifacts;

namespace Morsa.Infrastructure.Metadata;

/// <summary>Shared helpers keep extractor output stable and comparable.</summary>
internal static class MetadataUtilities
{
    public static MetadataObservation Observation(
        Guid artifactId,
        string category,
        string value,
        string extractor,
        string version,
        string? location = null,
        double confidence = 1.0)
    {
        return new MetadataObservation
        {
            ArtifactId = artifactId,
            Category = category,
            OriginalValue = value,
            NormalizedValue = Normalize(category, value),
            Extractor = extractor,
            ExtractorVersion = version,
            Location = location,
            Confidence = confidence,
        };
    }

    public static string Normalize(string category, string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return category switch
        {
            "email" or "hostname" or "server" or "url" => normalized.ToLowerInvariant(),
            "date" when DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed) => parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            _ => normalized,
        };
    }

    public static bool IsSafeZipPath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        return !Path.IsPathRooted(normalized) &&
               !normalized.Split('/').Any(segment => segment == "..");
    }
}

