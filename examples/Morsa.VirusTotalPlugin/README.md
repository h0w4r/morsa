# Morsa VirusTotal provider

Optional external `morsa-plugin/1` adapter for VirusTotal API v3.

Provider references: [file report](https://docs.virustotal.com/reference/file-info) and [file upload](https://docs.virustotal.com/reference/files-scan).

## Operations

- `hash_lookup` — default passive operation. Input: `{ "hash": "<md5|sha1|sha256>" }`.
- `upload` — sends a local file only when both `path` and `explicit_upload: true` are supplied. Direct uploads are limited to 32 MiB.

No operation returns the API key, provider request URL, local full path, raw exception, or unbounded provider response.

## Configuration

```bash
export VT_API_KEY='replace-me'
# Optional fixture endpoint. HTTPS is required except for loopback HTTP.
export VT_API_BASE_URL='http://127.0.0.1:8080/api/v3/'
```

Both variables are declared in `secretEnvironmentVariables` because the Morsa process host starts plugins with an environment allowlist.

## Build and protocol smoke test

```bash
dotnet build Morsa.VirusTotalPlugin.csproj -c Release
printf '%s\n%s\n' \
  '{"type":"initialize","protocol":"morsa-plugin/1"}' \
  '{"type":"request","id":"vt-1","operation":"hash_lookup","input":{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}' \
  | dotnet run --project Morsa.VirusTotalPlugin.csproj --no-build -c Release
```

Use a loopback fixture for tests. Without `VT_API_KEY`, the request returns a structured `configuration_invalid` protocol error.

## Publish and install

```bash
dotnet publish Morsa.VirusTotalPlugin.csproj -c Release -r linux-x64
morsa plugin install ./publish-directory
```

The project copies `morsa-plugin.json` beside the executable during build and publish.
