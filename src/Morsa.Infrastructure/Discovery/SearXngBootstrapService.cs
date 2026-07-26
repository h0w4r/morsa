using System.Security.Cryptography;

namespace Morsa.Infrastructure.Discovery;

/// <summary>Generates a loopback-only SearXNG Compose deployment with JSON enabled.</summary>
public sealed class SearXngBootstrapService
{
    public async Task<IReadOnlyList<string>> GenerateAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var compose = """
            services:
              redis:
                image: docker.io/valkey/valkey:8-alpine
                restart: unless-stopped
              searxng:
                image: docker.io/searxng/searxng:latest
                restart: unless-stopped
                ports:
                  - "127.0.0.1:8080:8080"
                volumes:
                  - ./settings.yml:/etc/searxng/settings.yml:ro
                depends_on:
                  - redis
                healthcheck:
                  test: ["CMD", "wget", "-qO-", "http://127.0.0.1:8080/healthz"]
                  interval: 30s
                  timeout: 5s
                  retries: 5
            """;
        var settings = $$"""
            use_default_settings: true
            general:
              instance_name: "Morsa SearXNG"
            server:
              bind_address: "0.0.0.0"
              port: 8080
              secret_key: "{{secret}}"
              limiter: true
            search:
              formats:
                - html
                - json
              safe_search: 0
            redis:
              url: redis://redis:6379/0
            outgoing:
              request_timeout: 10.0
              max_request_timeout: 20.0
              pool_connections: 20
              pool_maxsize: 10
            """;
        var composePath = Path.Combine(root, "compose.yaml");
        var settingsPath = Path.Combine(root, "settings.yml");
        await File.WriteAllTextAsync(composePath, compose, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(settingsPath, settings, cancellationToken).ConfigureAwait(false);
        return [composePath, settingsPath];
    }
}

