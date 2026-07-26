# Servidor MCP

`morsa-mcp` usa los mismos servicios que la CLI mediante el SDK MCP estable y
transporte exclusivamente por entrada/salida estándar. No abre puertos. stdout
queda reservado al protocolo y todos los logs van por stderr.

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

## Tools disponibles

| Grupo | Tools |
|---|---|
| Proyecto | `morsa_project_init`, `morsa_project_status` |
| Alcance | `morsa_scope_add`, `morsa_scope_list` |
| Artefactos | `morsa_ingest_file`, `morsa_ingest_url`, `morsa_analyze` |
| Discovery | `morsa_discover_documents`, `morsa_fetch_pending` |
| Correlación | `morsa_correlate`, `morsa_get_entities`, `morsa_get_findings` |
| Recon | `morsa_recon_dns`, `morsa_fingerprint_http`, `morsa_fingerprint_tls`, `morsa_fingerprint_banner` |
| Web/malware | `morsa_web_crawl`, `morsa_malware_scan` |
| Export | `morsa_export_graph`, `morsa_export_report` |
| Pipeline | `morsa_run_full`, `morsa_run_resume` |

Las rutas se canonicalizan. La ingesta local queda confinada al workspace;
reportes y grafos solo escriben bajo `reports/`; toda tool activa reutiliza alcance
y SSRF. Entidades/hallazgos usan `offset`/`limit` con máximo 10,000; crawl limita
profundidad 0–10 y páginas 1–100,000; URL limita MiB y redirects.

Secuencia recomendada: `morsa_project_init`, `morsa_scope_add`, ingesta/análisis o
`morsa_run_full`, `morsa_project_status`, lectura paginada y exportación. Tras una
interrupción usa `morsa_run_resume`. Toda respuesta lleva `schema_version`; un error
de protocolo es error MCP, nunca un resultado vacío inventado.
