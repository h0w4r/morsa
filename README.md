# Morsa

Morsa is a Linux-first CLI for metadata extraction, public-resource discovery,
evidence correlation, and explicitly scoped reconnaissance. It is inspired by
FOCA and implemented as a .NET 10 modular monolith with separate parser and
plugin processes.

[Documentación en español](README.es.md) · [Documentation index](docs/README.md) ·
[Security policy](SECURITY.md) · [CLI reference](docs/en/cli-reference.md)

## What ships in 1.0

- SHA-256 content-addressable artifact ingestion and durable SQLite runs/tasks.
- Bounded metadata extraction for OOXML, ODF, PDF, images, SVG, RDP, ICA,
  OLE/CFB, InDesign, WordPerfect, extensionless, and unknown artifacts.
- Discovery through DuckDuckGo HTML/Lite, SearXNG, Common Crawl, sitemaps,
  robots, direct crawl, and generic import paths.
- Scoped acquisition with redirect, SSRF, byte, concurrency, and request budgets.
- HTTP, HTTPS CONNECT, SOCKS4, SOCKS5, and SOCKS5h proxy pools with automatic
  rotation, cooldown, leases, sticky identity isolation, and bounded fallback.
- Evidence-backed entities, relationships, timelines, JSON/CSV/HTML reports,
  GraphML/GEXF/DOT graphs, and reproducible evidence bundles.
- DNS, TLS, HTTP and bounded banner fingerprinting; web mapping; local malware,
  ClamAV, and YARA integrations.
- Versioned process plugins (`morsa-plugin/1`) and a stdio-only MCP server.
- Self-contained x64/ARM64 artifacts for glibc and musl, DEB/RPM packages, and a
  multi-architecture OCI image.

The exact FOCA compatibility status is tracked without marketing fog in the
[parity matrix](docs/en/foca-parity.md). A Windows CI gate compiles the pinned FOCA
commit and differentially checks both extractors over the same deterministic corpus.

## Supported Linux targets

| RID | ABI | Architecture | Delivery |
|---|---|---|---|
| `linux-x64` | glibc | x86-64 | tar.gz, DEB, RPM |
| `linux-arm64` | glibc | ARM64 | tar.gz, DEB, RPM |
| `linux-musl-x64` | musl | x86-64 | tar.gz, OCI |
| `linux-musl-arm64` | musl | ARM64 | tar.gz, OCI |

Clean-container smoke workflows cover Debian, Ubuntu, Kali, Fedora, Arch, and
Alpine. WSL is useful for development but is neither required nor treated as a
runtime platform.

## Install a release

Download the matching archive and `SHA256SUMS` from the GitHub release, then:

```bash
sha256sum --check SHA256SUMS --ignore-missing
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo bash ./morsa-1.0.0-linux-x64/install.sh
morsa doctor
```

DEB, RPM, OCI, unprivileged-prefix, verification, and uninstall procedures are
documented in [installation](docs/en/installation.md) and
[release verification](docs/en/release-verification.md).

## Five-minute workflow

```bash
mkdir investigation && cd investigation
morsa init . --name example

# Active commands are allowed only after scope is explicit.
morsa scope add example.org --kind domain --max-mode active

# Local evidence path: no network required.
morsa ingest file ./document.pdf
morsa analyze all
morsa correlate
morsa report html --output ./reports/report.html

# Full discovery/acquisition path with durable resume.
morsa run full example.org --providers duckduckgo,commoncrawl
morsa run resume
```

Every machine-readable result carries `schema_version`. Human output goes to
stdout, structured logs to stderr, and MCP never writes diagnostics to its
stdout transport.

## Proxy pools and automatic rotation

```bash
morsa proxy pool add research \
  --policy least-latency \
  --max-rotations 5 \
  --max-attempts 8

morsa proxy import ./proxies.ndjson --pool research
morsa proxy test research --url https://example.org/
morsa run full example.org --proxy-pool research
```

Rotation is bounded and triggered by transport/DNS/TLS failures, proxy auth
failure, configured `403`, `429`, `5xx`, or a detected challenge. Direct fallback
is never implicit when a profile requires a proxy. See the
[proxy guide](docs/en/proxies.md).

## Build from source

The repository pins SDK `10.0.302` in `global.json`:

```bash
bash scripts/install-dotnet.sh
export PATH="$PWD/.dotnet:$PATH"
dotnet restore Morsa.slnx --disable-parallel
dotnet build Morsa.slnx -c Release --no-restore
dotnet test Morsa.slnx -c Release --no-build
```

Build one deterministic self-contained payload:

```bash
bash scripts/build-release.sh --version 1.0.0 --rid linux-x64
```

## Supply-chain verification

Releases provide SHA-256 manifests, SPDX and CycloneDX SBOMs, and GitHub
artifact attestations signed through short-lived Sigstore/OIDC identity.

```bash
sha256sum --check SHA256SUMS
gh attestation verify morsa-1.0.0-linux-x64.tar.gz -R h0w4r/morsa
```

## Documentation

- [Architecture](docs/en/architecture.md)
- [CLI reference](docs/en/cli-reference.md)
- [Scope and activity levels](docs/en/scope.md)
- [Proxy pools](docs/en/proxies.md)
- [Plugin SDK and JSONL protocol](docs/en/plugins.md)
- [MCP server](docs/en/mcp.md)
- [Threat model](docs/en/threat-model.md)
- [FOCA parity matrix](docs/en/foca-parity.md)
- [Upgrade guide](docs/en/upgrade.md)
- [Troubleshooting](docs/en/troubleshooting.md)

## License and upstream provenance

Morsa is licensed under `GPL-3.0-or-later`. Its compatibility baseline is FOCA
`v3.4.7.1`, commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`.
Selective ports must retain attribution, source path, upstream commit, and
recorded hashes as described in `NOTICE.md`.
