# Troubleshooting

Start with evidence, not assumptions:

```bash
morsa version --json
morsa doctor --project /path/to/workspace --json
morsa project status --project /path/to/workspace --json
```

Keep stdout and stderr separately when automation fails:

```bash
morsa run resume --project /cases/acme --json \
  >result.json 2>diagnostic.log
```

## Binary does not start

| Symptom | Check | Action |
|---|---|---|
| `Exec format error` | `uname -m`, `file /path/morsa` | install matching x64/ARM64 RID |
| `not found` although file exists | `ldd --version`, `file` | use musl artifact on Alpine, glibc elsewhere |
| ICU/globalization error | package manager inventory | install `icu-libs`/`libicu` package |
| TLS CA failures everywhere | `/etc/ssl/certs`, CA package | install/update `ca-certificates`; do not disable TLS |
| permission denied | `stat`, mount options | restore executable bit; do not run from `noexec` mount |

## Workspace/SQLite

- Confirm the selected `--project` path and ownership.
- Stop duplicate writers if the filesystem does not support reliable POSIX locks.
- Avoid network filesystems for active SQLite WAL workspaces.
- Preserve `morsa.db`, `-wal`, and `-shm` together before repair.
- Use `run resume`; do not manually mark `Task.Status` in SQLite.
- A partial status is intentional: inspect task diagnostics before retrying.

## Scope rejected

```bash
morsa scope list --project /cases/acme --json
```

Compare normalized target, port/scheme/path, resolved addresses, and requested mode.
Redirects need their own authorization. A proxy or `NO_PROXY` setting cannot widen
scope. Add the narrow intended entry rather than a universal CIDR.

## Proxy problems

```bash
morsa proxy status --project /cases/acme --pool egress --json
morsa proxy test egress --project /cases/acme --url https://example.org/
```

- `407`: confirm the `secret_ref` environment variable exists in the Morsa process
  and contains `username:password`; do not put it in the URI.
- `cooldown`: inspect failure outcome and `Retry-After`; wait or test another pool.
- SOCKS name failure: verify whether the endpoint uses `socks5` (local DNS) or
  `socks5h` (remote DNS).
- unexpected direct connection: inspect `AllowDirectFallback`, `NO_PROXY`, and
  report `NetworkAttempt` records.
- every proxy fails identically: treat it as provider/target transversal blocking,
  not a reason to remove finite retry bounds.

## Discovery providers

```bash
morsa provider status --json
morsa provider bootstrap searxng --output ./provider
```

Set `MORSA_SEARXNG_URL` to the explicit base URL. Common Crawl index availability
and public HTML layouts can change; provider diagnostics show partial coverage.
One failed provider does not mean the persisted result set is empty.

## Parsers

- Check artifact size and extraction diagnostics.
- `zip.entry_budget`, `zip.size_budget`, and XML errors are security limits, not
  requests to disable bounds.
- `sandbox=strict` requires the configured OS isolation; `auto` reports degradation.
- Reproduce with a copy and retain SHA-256; never modify the original evidence.
- A binary-string fallback is expected to be lower-confidence than a semantic parser.

## Plugins

```bash
morsa plugin list --json
morsa plugin run ID health --input '{}' --timeout 10
```

Validate `apiVersion`, kind, entry-point path/mode, SHA-256, and declared permissions.
Protocol output is one JSON object per stdout line. Diagnostic chatter belongs on
stderr. A timeout is persisted as `PLUGIN_TIMEOUT`; fix the plugin or rollback.

## MCP

- Launch the absolute `morsa-mcp` libexec path.
- Do not wrap it with a shell that writes banners to stdout.
- Capture stderr separately.
- Use absolute workspace paths and outputs below `reports/`.
- Page entity/finding reads rather than requesting unbounded responses.
- Test the equivalent CLI operation to distinguish adapter from application failure.

## Release/build

Run Bash syntax and release script checks:

```bash
find scripts -name '*.sh' -print0 | xargs -0 -n1 bash -n
bash scripts/build-release.sh --version 1.0.0-test --rid linux-x64
```

If `dotnet restore` appears stalled, use `--disable-parallel`, verify NuGet TLS/DNS,
and retain the actual error. Never disable package vulnerability audit merely to
make a release green.
