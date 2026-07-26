# Configuration reference

Morsa reads `morsa.toml` from the selected workspace. If that file does not exist,
it falls back to `$XDG_CONFIG_HOME/morsa/config.toml` or
`~/.config/morsa/config.toml`. A project file replaces the global file; configuration
is validated before any command executes and is reloaded on the next process start.

```toml
[project]
name = "authorized-case"
default_mode = "passive"

[output]
color = true
format = "table" # table, json, or ndjson

[network]
concurrency = 8
requests_per_second = 2.0
timeout_seconds = 15
max_redirects = 5
query_budget = 500

[artifacts]
max_download_mb = 100
max_uncompressed_mb = 500
sandbox = "auto" # auto, strict, or off

[security]
allow_private_networks = false
redact_sensitive_values = true

[proxy_profiles.egress]
policy = "sticky"
max_rotations = 5
max_attempts = 8
cooldown_seconds = 120
lease_ttl_seconds = 900
allow_direct_fallback = false
```

## Effective behavior

- `network.requests_per_second` is one global target limiter shared by every proxy.
- `network.query_budget`, redirect and timeout values bound discovery, acquisition,
  parser and full-pipeline work where applicable.
- artifact byte budgets govern ingestion, extraction and the resumable full pipeline.
- `artifacts.sandbox=auto` prefers Bubblewrap, then a locally cached Podman/Docker
  image, and otherwise reports degradation to a restricted subprocess.
- `artifacts.sandbox=strict` fails closed unless Bubblewrap or that local OCI
  boundary is usable. OCI execution uses `--pull=never`; parsing never pulls an image.
- JSON/NDJSON configuration affects stdout only; structured logs remain on stderr/files.
- NDJSON emits one versioned envelope per collection item.
- reports pseudonymize sensitive values by default; `--include-sensitive` is explicit.
- proxy profiles upsert pool policy in SQLite but never place credentials there.

`MORSA_REQUESTS_PER_SECOND`, `MORSA_SANDBOX`, and `MORSA_PARSER_OCI_IMAGE` are
explicit process-level overrides. The OCI image reference must already exist locally.
Standard proxy variables and `NO_PROXY` keep their documented precedence.

## Safety bounds

Invalid values fail before execution. Important hard ceilings include 1,024 concurrent
workers, 1,000 requests/second, 20 redirects, 2,047 MiB per download, 100 GiB of
uncompressed parser data, 100,000 query results, and bounded proxy attempts/leases.
