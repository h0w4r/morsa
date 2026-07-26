using System.Text.Json;
using Tomlyn;

namespace Morsa.Infrastructure.Configuration;

/// <summary>Typed configuration shared by CLI, MCP and worker hosts.</summary>
public sealed class MorsaConfiguration
{
    public ProjectConfiguration Project { get; set; } = new();

    public OutputConfiguration Output { get; set; } = new();

    public NetworkConfiguration Network { get; set; } = new();

    public ArtifactConfiguration Artifacts { get; set; } = new();

    public SecurityConfiguration Security { get; set; } = new();

    public Dictionary<string, ProxyProfileConfiguration> ProxyProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProjectConfiguration
{
    public string Name { get; set; } = "morsa-project";

    public string DefaultMode { get; set; } = "passive";
}

public sealed class OutputConfiguration
{
    public bool Color { get; set; } = true;

    public string Format { get; set; } = "table";
}

public sealed class NetworkConfiguration
{
    public int Concurrency { get; set; } = 8;

    public double RequestsPerSecond { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 15;

    public int MaxRedirects { get; set; } = 5;

    public int QueryBudget { get; set; } = 100;
}

public sealed class ArtifactConfiguration
{
    public int MaxDownloadMb { get; set; } = 100;

    public int MaxUncompressedMb { get; set; } = 500;

    public string Sandbox { get; set; } = "auto";
}

public sealed class SecurityConfiguration
{
    public bool AllowPrivateNetworks { get; set; }

    public bool RedactSensitiveValues { get; set; } = true;
}

public sealed class ProxyProfileConfiguration
{
    public string Policy { get; set; } = "sticky";

    public int MaxRotations { get; set; } = 5;

    public int MaxAttempts { get; set; } = 8;

    public int CooldownSeconds { get; set; } = 120;

    public int LeaseTtlSeconds { get; set; } = 900;

    public bool AllowDirectFallback { get; set; }
}

/// <summary>Loads TOML with deterministic snake_case mapping and bounded depth.</summary>
public static class MorsaConfigurationLoader
{
    private static readonly TomlSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        MaxDepth = 32,
    };

    public static async Task<MorsaConfiguration> LoadAsync(
        string configurationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configurationPath))
        {
            return new MorsaConfiguration();
        }

        var toml = await File.ReadAllTextAsync(configurationPath, cancellationToken).ConfigureAwait(false);
        return TomlSerializer.Deserialize<MorsaConfiguration>(toml, SerializerOptions) ??
               new MorsaConfiguration();
    }

    public static Task SaveAsync(
        string configurationPath,
        MorsaConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var toml = TomlSerializer.Serialize(configuration, SerializerOptions);
        return File.WriteAllTextAsync(configurationPath, toml, cancellationToken);
    }
}

