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
    private const int MaximumConfigurationBytes = 1024 * 1024;

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

        var info = new FileInfo(configurationPath);
        if (info.Length > MaximumConfigurationBytes)
            throw new InvalidDataException($"Morsa configuration exceeds {MaximumConfigurationBytes} bytes.");
        var toml = await File.ReadAllTextAsync(configurationPath, cancellationToken).ConfigureAwait(false);
        return Validate(TomlSerializer.Deserialize<MorsaConfiguration>(toml, SerializerOptions) ??
                        new MorsaConfiguration());
    }

    /// <summary>Loads a project configuration when present, otherwise the XDG user configuration.</summary>
    public static MorsaConfiguration LoadForWorkspace(string workspacePath)
    {
        var projectPath = Path.Combine(Path.GetFullPath(workspacePath), "morsa.toml");
        var configurationHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configurationHome))
        {
            configurationHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        var globalPath = Path.Combine(configurationHome, "morsa", "config.toml");
        var selected = File.Exists(projectPath) ? projectPath : globalPath;
        return LoadAsync(selected).GetAwaiter().GetResult();
    }

    public static Task SaveAsync(
        string configurationPath,
        MorsaConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var toml = TomlSerializer.Serialize(configuration, SerializerOptions);
        return File.WriteAllTextAsync(configurationPath, toml, cancellationToken);
    }

    private static MorsaConfiguration Validate(MorsaConfiguration configuration)
    {
        if (configuration.Network.Concurrency is < 1 or > 1024)
            throw new InvalidDataException("network.concurrency must be between 1 and 1024.");
        if (configuration.Network.RequestsPerSecond is < 0.1 or > 1000)
            throw new InvalidDataException("network.requests_per_second must be between 0.1 and 1000.");
        if (configuration.Network.TimeoutSeconds is < 1 or > 3600 || configuration.Network.MaxRedirects is < 0 or > 20)
            throw new InvalidDataException("Network timeout or redirect budget is outside its safety bounds.");
        if (configuration.Network.QueryBudget is < 1 or > 1_000_000)
            throw new InvalidDataException("network.query_budget must be between 1 and 1000000.");
        if (configuration.Artifacts.MaxDownloadMb is < 1 or > 2_047 || configuration.Artifacts.MaxUncompressedMb is < 1 or > 102_400)
            throw new InvalidDataException("Artifact byte budgets are outside their safety bounds.");
        if (configuration.Artifacts.Sandbox is not ("auto" or "strict" or "off"))
            throw new InvalidDataException("artifacts.sandbox must be auto, strict or off.");
        if (configuration.Project.DefaultMode is not ("passive" or "active" or "aggressive"))
            throw new InvalidDataException("project.default_mode must be passive, active or aggressive.");
        if (configuration.Output.Format is not ("table" or "json" or "ndjson"))
            throw new InvalidDataException("output.format must be table, json or ndjson.");
        foreach (var (name, profile) in configuration.ProxyProfiles)
        {
            if (string.IsNullOrWhiteSpace(name) || profile.MaxRotations is < 0 or > 1000 ||
                profile.MaxAttempts is < 1 or > 10_000 || profile.CooldownSeconds is < 0 or > 86_400 ||
                profile.LeaseTtlSeconds is < 1 or > 604_800 ||
                !Enum.TryParse<Morsa.Domain.Networking.ProxySelectionPolicy>(profile.Policy.Replace("-", string.Empty), true, out _))
                throw new InvalidDataException($"Proxy profile '{name}' contains an invalid budget.");
        }

        return configuration;
    }
}
