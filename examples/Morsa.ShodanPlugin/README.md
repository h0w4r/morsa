# Morsa Shodan provider

Optional external `morsa-plugin/1` adapter for the Shodan REST API.

Provider reference: [Shodan Host Information API](https://developer.shodan.io/api#shodan-host).

## Operation

`host_lookup` accepts an IP literal and the optional official `history` and `minify` flags:

```json
{"ip":"203.0.113.10","history":false,"minify":false}
```

The response contains bounded host fields and at most 512 normalized service banners. The API key, complete request URL, raw exception and unbounded provider response are never returned.

## Configuration

```bash
export SHODAN_API_KEY='replace-me'
# Optional fixture endpoint. HTTPS is required except for loopback HTTP.
export SHODAN_API_BASE_URL='http://127.0.0.1:8080/'
```

Both variables are declared in `secretEnvironmentVariables` because the Morsa process host starts plugins with an environment allowlist.

## Build and protocol smoke test

```bash
dotnet build Morsa.ShodanPlugin.csproj -c Release
printf '%s\n%s\n' \
  '{"type":"initialize","protocol":"morsa-plugin/1"}' \
  '{"type":"request","id":"shodan-1","operation":"host_lookup","input":{"ip":"203.0.113.10","minify":true}}' \
  | dotnet run --project Morsa.ShodanPlugin.csproj --no-build -c Release
```

Use a loopback fixture for tests. Without `SHODAN_API_KEY`, the request returns a structured `configuration_invalid` protocol error.

## Publish and install

```bash
dotnet publish Morsa.ShodanPlugin.csproj -c Release -r linux-x64
morsa plugin install ./publish-directory
```

The project copies `morsa-plugin.json` beside the executable during build and publish.
