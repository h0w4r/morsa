# MCP server

`morsa-mcp` exposes the same application services as the CLI using the stable MCP
SDK and standard input/output transport only. It does not open a TCP port. stdout
is reserved for MCP frames; all logs go to stderr.

## Client configuration

Example for a client that accepts a command, arguments, and environment:

```json
{
  "mcpServers": {
    "morsa": {
      "command": "/usr/local/libexec/morsa/morsa-mcp",
      "args": [],
      "env": {
        "MORSA_PARSER_HOST": "/usr/local/libexec/morsa/morsa-parser-host",
        "MORSA_PLUGIN_HOST": "/usr/local/libexec/morsa/morsa-plugin-host"
      }
    }
  }
}
```

Adjust the libexec prefix for DEB/RPM (`/usr/libexec/morsa`) or an unprivileged
installation. Pass workspace paths as absolute paths whenever possible.

## Tools

| Tool | Purpose | Activity |
|---|---|---|
| `morsa_project_init` | create/open workspace | local write |
| `morsa_project_status` | durable counters and run states | read |
| `morsa_scope_add` | add/update authorized scope | policy write |
| `morsa_scope_list` | list scope | read |
| `morsa_ingest_file` | ingest a workspace-confined file | local write |
| `morsa_ingest_url` | acquire one in-scope URL | active |
| `morsa_discover_documents` | multi-provider discovery | passive/optional active crawl |
| `morsa_fetch_pending` | fetch pending resources | active |
| `morsa_analyze` | extract pending/selected artifact | local parser |
| `morsa_correlate` | create entities and relations | local write |
| `morsa_recon_dns` | query selected DNS types | active |
| `morsa_fingerprint_http` | bounded HTTP evidence | active |
| `morsa_fingerprint_tls` | TLS/certificate evidence | active |
| `morsa_fingerprint_banner` | bounded TCP banner | active |
| `morsa_web_crawl` | bounded same-host crawl | active |
| `morsa_malware_scan` | local static/optional ClamAV | local process |
| `morsa_get_entities` | paged entities | read |
| `morsa_get_findings` | paged findings | read |
| `morsa_export_graph` | DOT/GraphML/GEXF/CSV below reports | local write |
| `morsa_export_report` | redacted JSON/HTML below reports | local write |
| `morsa_run_full` | complete durable pipeline | mixed |
| `morsa_run_resume` | resume pending pipeline | mixed |

## Path confinement

- Workspace roots are canonicalized.
- `morsa_ingest_file` accepts relative paths only below the workspace or validates
  an absolute path against it.
- Graph/report outputs are confined below the workspace `reports/` directory.
- Plugin and artifact store paths are not exposed as arbitrary write primitives.
- The MCP adapter reuses scope and SSRF validation for every active tool.

## Bounds and pagination

`morsa_get_entities` and `morsa_get_findings` take `offset` and `limit`; the limit
cannot exceed 10,000. Crawl depth is 0–10 and pages 1–100,000. URL ingestion has
explicit MiB and redirect limits. Discovery limits results and parser operations
support cancellation.

## Example sequence

1. Call `morsa_project_init` with an empty workspace path.
2. Call `morsa_scope_add` with `maximum_mode: active`.
3. Call `morsa_ingest_file` and `morsa_analyze` for local evidence, or
   `morsa_run_full` for a target.
4. Poll `morsa_project_status` or use `morsa_run_resume` after interruption.
5. Read `morsa_get_entities`/`morsa_get_findings` in bounded pages.
6. Export with `morsa_export_report` or `morsa_export_graph`.

All structured responses include `schema_version`. A protocol error is returned as
an MCP error; it is never disguised as an empty successful result.
