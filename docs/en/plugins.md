# Plugins

Morsa supports versioned external process packages and a managed SDK contract.
Version 1.0 executes installed packages as processes using newline-delimited JSON;
plugin failure cannot terminate the CLI.

## Package layout

```text
example-plugin/
├── morsa-plugin.json
├── bin/example-plugin
└── LICENSE
```

`morsa-plugin.json` uses web-style property names and API version `1`:

```json
{
  "id": "example.reputation",
  "name": "Example Reputation",
  "version": "1.2.0",
  "author": "Example",
  "apiVersion": "1",
  "kind": "process",
  "entryPoint": "bin/example-plugin",
  "arguments": [],
  "permissions": ["network"],
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "Looks up an explicitly supplied hash."
}
```

The reader is case-insensitive, but producers should use the shown camelCase.
Valid IDs match `^[a-z0-9][a-z0-9._-]{1,63}$`. `entryPoint` must be relative and
remain inside the package. Kinds are `process` and `dotnet-process`.

Allowed declared permissions are:

- `network`
- `filesystem:read`
- `filesystem:write`
- `secrets`
- `process`

Unknown permissions, invalid hashes, missing entry points, absolute paths, and
ZIP traversal are rejected. A SHA-256 field is strongly recommended.

## Lifecycle

```bash
morsa plugin install ./example-plugin.zip
morsa plugin list --json
morsa plugin activate example.reputation 1.2.0
morsa plugin run example.reputation hash_lookup \
  --input '{"sha256":"..."}' --timeout 30
morsa plugin rollback example.reputation
morsa plugin remove example.reputation --version 1.2.0
```

Packages are staged below `.morsa/plugins/.staging`, validated, then atomically
moved to `.morsa/plugins/ID/VERSION`. `current.txt` is an atomic active-version
pointer. Rollback activates the highest earlier valid version. Removal does not
load or execute package code.

## `morsa-plugin/1` JSONL protocol

Morsa starts the plugin without a shell, clears inherited environment variables,
then supplies a constrained environment including `PATH`, `LANG`,
`MORSA_PLUGIN_PROTOCOL`, `MORSA_WORKSPACE`, and `MORSA_PLUGIN_PERMISSIONS`.

Initialization message:

```json
{"type":"initialize","protocol":"morsa-plugin/1","plugin_id":"example.reputation","permissions":["network"]}
```

Request:

```json
{"type":"request","id":"42f...","operation":"hash_lookup","input":{"sha256":"..."}}
```

Result or error:

```json
{"type":"result","id":"42f...","output":{"known":false}}
{"type":"error","id":"42f...","error":{"code":"UPSTREAM_TIMEOUT","message":"provider timed out"}}
```

Each JSON value occupies exactly one UTF-8 line. A plugin may emit one initialized
acknowledgement before the result. Morsa reads at most 16 protocol messages, limits
a response line to 4 MiB, stderr to 1 MiB, and enforces the requested timeout.
Protocol text belongs on stdout; diagnostics belong on stderr.

## Managed SDK

`Morsa.PluginSdk` exposes `IMorsaPlugin`, `PluginManifest`, and
`IMorsaPluginRegistry`. The registry is capability-oriented: managed plugins can
register `IArtifactExtractor` and `ISearchProvider`, not resolve arbitrary runtime
services. Managed SDK compatibility still uses manifest API version `1`.

## Optional provider examples

The repository includes complete external adapters that exercise the same
`morsa-plugin/1` boundary used by third parties:

- `examples/Morsa.VirusTotalPlugin`: hash lookup by default; file upload exists
  only as the separate `upload` operation and requires `explicit_upload=true`.
  Credentials are read from `VT_API_KEY`, never from the manifest or SQLite.
- `examples/Morsa.ShodanPlugin`: bounded `host_lookup` for an explicit IP address.
  Credentials are read from `SHODAN_API_KEY` and are redacted from errors.

Both providers support loopback-only base URL overrides for fixture tests, reject
arbitrary clear-text remote endpoints, cap provider responses, and ship bilingual
build/operation instructions in their own directories. Unit/integration tests use
local HTTP fixtures; release gates never require or exercise a real user secret.

## Security guidance

- Review source and verify the entry-point hash before installation.
- Grant the smallest permission set; declarations are audit inputs, not a reason
  to run an unknown binary without OS sandbox controls.
- Package no credentials. Use configured secret references at runtime.
- Make requests idempotent because orchestration can resume after interruption.
- Bound memory, output, subprocesses, network calls, and filesystem traversal.
- Never emit evidence or credentials to stderr merely because it is not stdout.
