# Referencia CLI

Esta página documenta el árbol estable de Morsa 1.0. Ejecuta
`morsa COMMAND --help` para ver la ayuda generada por la versión instalada.

## Contrato global

```text
morsa [--project PATH] [--json] COMMAND [ARGUMENTS] [OPTIONS]
```

`--project` selecciona workspace; por defecto se usa el directorio actual.
`--json` produce contrato con `schema_version`; logs y diagnósticos van por stderr.

| Exit | Significado |
|---:|---|
| 0 | éxito completo |
| 1 | fallo no clasificado |
| 3 | rechazo de política/alcance/input en comandos acotados |
| 5 | ejecución parcial con diagnóstico persistido |
| 7 | cancelación cooperativa |
| 8 | catálogo de plugins contiene versión inválida |
| 9 | fallo de runtime/host de plugins |
| 10 | gate de doctor o salud de base falló |

La sintaxis incorrecta también devuelve no-cero. Automatiza usando cero/no-cero y
el código JSON, no suponiendo causa desde un entero no documentado.

## Workspace y alcance

```bash
morsa init [PATH] [--name NAME]
morsa doctor [--project PATH] [--json]
morsa version [--json]
morsa project status [--project PATH] [--json]
morsa scope add VALUE [--kind domain|host|url|ip|cidr] \
  [--max-mode passive|active|aggressive]
morsa scope list [--json]
```

## Ingesta y metadatos

```bash
morsa ingest file FILE [--max-mb 100]
morsa ingest directory DIRECTORY [--recursive] [--max-files 10000]
morsa analyze all
morsa correlate
```

La ingesta usa magic bytes/MIME, calcula SHA-256, deduplica y almacena contenido.
El análisis toma trabajo pendiente; correlación crea relaciones con evidencia.

## Discovery, adquisición y pipeline

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

`run full` descubre, adquiere, analiza y correlaciona. `run resume` continúa tareas
durables después de interrupción. El fallo de un provider no borra resultados de
los demás.

## Proxies

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

`SOURCE` acepta archivo TXT/CSV/JSON/NDJSON, `-`, `env`, URL HTTPS o
`command:/ruta/ejecutable`. Revisa [proxies](proxies.md).

## DNS, fingerprint, web y malware

```bash
morsa recon dns NAME [--types A,AAAA,MX,NS,SOA,TXT,CNAME,SRV,CAA]
morsa recon reverse ADDRESS[,ADDRESS...]
morsa recon subdomains DOMAIN [--wordlist FILE] [--budget 1000]
morsa recon range CIDR [--budget 4096]
morsa recon axfr ZONE [--server NAME]
morsa fingerprint http URL [--proxy-pool POOL]
morsa fingerprint tls HOST [--port PORT]
morsa fingerprint banner HOST [--port PORT] [--protocol tcp]
morsa web crawl URL [--depth 3] [--max-pages 500] [--proxy-pool POOL]
morsa web backups URL [--budget 100] [--proxy-pool POOL]
morsa malware scan [--artifact UUID]
morsa malware yara RULES [--artifact UUID]
```

Los resultados se persisten como `DnsObservation`, `ServiceObservation` y
`MalwareObservation`. Backups es agresivo y presupuestado. ClamAV/YARA son locales;
estos comandos no suben muestras.

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

## Reportes

```bash
morsa report json|html|csv [--output FILE] [--include-sensitive]
morsa report bundle [--output FILE] [--redact] [--include-sensitive]
morsa graph export [--format graphml|gexf|dot|csv] [--output FILE]
```

Los reportes preservan IDs de evidencia, timeline cronológica, diagnósticos y
relaciones. Los valores sensibles se seudonimizan por defecto según
`security.redact_sensitive_values`; `--include-sensitive` desactiva esa protección
de forma explícita. Un bundle redactado omite payloads y es reproducible.
