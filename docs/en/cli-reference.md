# CLI reference

This page documents the stable Morsa 1.0 command tree. Run `morsa COMMAND --help`
for parser-generated help from the installed build.

## Global contract

```text
morsa [--project PATH] [--json] COMMAND [ARGUMENTS] [OPTIONS]
```

`--project PATH` selects a workspace; otherwise the current directory is used.
`--json` emits a machine contract with `schema_version`. Logs and diagnostics go
to stderr. `Ctrl+C` requests cooperative cancellation and durable checkpointing.

| Exit | Meaning |
|---:|---|
| 0 | complete success |
| 1 | unclassified command failure |
| 3 | policy/scope/input rejection used by bounded active commands |
| 5 | partial completion with persisted diagnostics |
| 7 | cooperative cancellation |
| 8 | installed plugin catalog contains an invalid version |
| 9 | plugin runtime/host failure |
| 10 | doctor or database health gate failed |

The CLI parser also returns a nonzero status for malformed syntax. Automation
should test zero versus nonzero and inspect the JSON error code rather than infer
the cause from an undocumented integer.

## Workspace and scope

```bash
morsa init [PATH] [--name NAME]
morsa doctor [--project PATH] [--json]
morsa version [--json]
morsa project status [--project PATH] [--json]
morsa scope add VALUE [--kind KIND] [--max-mode passive|active|aggressive]
morsa scope list [--json]
```

Kinds are inferred when omitted. Use explicit `domain`, `host`, `url`, `ip`, or
`cidr` when ambiguity matters. Adding scope authorizes nothing above `--max-mode`.

## Local ingestion and metadata

```bash
morsa ingest file FILE [--max-mb 100]
morsa ingest directory DIRECTORY [--recursive] [--max-files 10000]
morsa analyze all
morsa correlate
```

Ingestion identifies content by magic bytes/MIME, computes SHA-256, deduplicates,
and writes into the artifact store. `analyze all` processes only pending work;
`correlate` normalizes observations and creates evidence-backed relations.

## Discovery, acquisition, and pipeline

```bash
morsa discover documents TARGET \
  [--types pdf,doc,docx,xls,xlsx,ppt,pptx,odt,ods,odp,svg] \
  [--provider searxng,duckduckgo,commoncrawl] \
  [--proxy-pool POOL] [--max-results 100] [--active-crawl]

morsa discover history TARGET
morsa discover import SOURCE [--format text|csv|json|ndjson|har] \
  [--max-results 100000]
morsa fetch pending [--proxy-pool POOL] [--max-mb 100]
morsa fetch url URL [--proxy-pool POOL] [--max-mb 100]
morsa ingest url URL [--proxy-pool POOL] [--max-mb 100]

morsa provider list|status
morsa provider bootstrap searxng [--output DIRECTORY]

morsa run full TARGET [--types TYPES] [--providers PROVIDERS] \
  [--proxy-pool POOL] [--active-crawl]
morsa run resume [--proxy-pool POOL]
```

`run full` performs discovery, acquisition, analysis, and correlation. Provider
errors remain visible but do not erase successful results from other providers.
`run resume` continues pending durable tasks after interruption.

## Proxy management

```bash
morsa proxy pool add NAME \
  [--policy sticky|round-robin|random|weighted|least-latency|failover] \
  [--max-rotations 5] [--max-attempts 8] [--allow-direct-fallback]
morsa proxy pool list
morsa proxy import SOURCE [--pool default]
morsa proxy source list
morsa proxy source load SOURCE [--pool default]
morsa proxy status [--pool NAME]
morsa proxy reset [--pool NAME]
morsa proxy test POOL [--url https://example.com/]
```

`SOURCE` may be a text/CSV/JSON/NDJSON file, `-` for stdin, `env`, an HTTPS URL,
or `command:/absolute/executable`. See [proxies](proxies.md) before enabling direct
fallback or remote DNS behavior.

## DNS and fingerprinting

```bash
morsa recon dns NAME [--types A,AAAA,MX,NS,SOA,TXT,CNAME,SRV,CAA]
morsa recon reverse ADDRESS[,ADDRESS...]
morsa recon subdomains DOMAIN [--wordlist FILE] [--budget 1000]
morsa recon range CIDR [--budget 4096]
morsa recon axfr ZONE [--server NAME]
morsa fingerprint http URL [--proxy-pool POOL]
morsa fingerprint tls HOST [--port PORT]
morsa fingerprint banner HOST [--port PORT] [--protocol tcp]
```

DNS and service results are persisted as `DnsObservation` and
`ServiceObservation`. Active operations must match an adequate scope entry.

## Web mapping and local malware analysis

```bash
morsa web crawl URL [--depth 3] [--max-pages 500] [--proxy-pool POOL]
morsa web backups URL [--budget 100] [--proxy-pool POOL]
morsa malware scan [--artifact UUID]
morsa malware yara RULES [--artifact UUID]
```

Backup validation is aggressive activity and is budgeted. Malware scanning is
local by default; optional ClamAV/YARA executables are invoked without a shell.
No sample is uploaded by these commands.

## Plugins

```bash
morsa plugin list
morsa plugin inspect ID
morsa plugin install DIRECTORY_OR_ZIP [--no-activate]
morsa plugin update DIRECTORY_OR_ZIP [--no-activate]
morsa plugin activate ID VERSION
morsa plugin rollback ID
morsa plugin remove ID [--version VERSION]
morsa plugin run ID OPERATION [--input JSON] [--timeout 30]
```

Installation validates package paths, API version, permissions, entry point, and
optional SHA-256. Execution uses the `morsa-plugin/1` JSONL process protocol.

## Reporting and graphs

```bash
morsa report json [--output FILE] [--include-sensitive]
morsa report html [--output FILE] [--include-sensitive]
morsa report csv [--output DIRECTORY] [--include-sensitive]
morsa report bundle [--output FILE] [--redact] [--include-sensitive]
morsa graph export [--format graphml|gexf|dot|csv] [--output FILE]
```

Reports preserve evidence IDs, a chronological timeline, diagnostics, and complete
evidence relationships. Sensitive values are pseudonymized by default according to
`security.redact_sensitive_values`; `--include-sensitive` is the explicit opt-out.
Bundles omit artifact payloads while redacted and are deterministic for the same
workspace state and redaction policy.

## Common examples

```bash
# Strictly local metadata investigation
morsa init ./case-a --name case-a
morsa ingest directory ./samples --project ./case-a --recursive
morsa analyze all --project ./case-a
morsa report bundle --project ./case-a --redact

# Proxy-backed discovery
morsa proxy pool add egress --project ./case-a --policy sticky
morsa proxy import ./proxies.ndjson --project ./case-a --pool egress
morsa run full example.org --project ./case-a --proxy-pool egress

# NDJSON-friendly automation pattern
morsa project status --project ./case-a --json | jq -e '.schema_version == "1"'
```
