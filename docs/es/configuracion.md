# Referencia de configuración

Morsa lee `morsa.toml` del workspace seleccionado. Si no existe, usa
`$XDG_CONFIG_HOME/morsa/config.toml` o `~/.config/morsa/config.toml`. El archivo del
proyecto reemplaza al global; la configuración se valida antes de ejecutar y se vuelve
a leer al iniciar el siguiente proceso.

```toml
[project]
name = "caso-autorizado"
default_mode = "passive"

[output]
color = true
format = "table" # table, json o ndjson

[network]
concurrency = 8
requests_per_second = 2.0
timeout_seconds = 15
max_redirects = 5
query_budget = 500

[artifacts]
max_download_mb = 100
max_uncompressed_mb = 500
sandbox = "auto" # auto, strict u off

[security]
allow_private_networks = false
redact_sensitive_values = true

[proxy_profiles.salida]
policy = "sticky"
max_rotations = 5
max_attempts = 8
cooldown_seconds = 120
lease_ttl_seconds = 900
allow_direct_fallback = false
```

## Comportamiento efectivo

- `network.requests_per_second` limita globalmente al objetivo, no por proxy.
- query, redirects y timeout acotan discovery, adquisición, parser y pipeline.
- los presupuestos de artefactos gobiernan ingesta, extracción y reanudación.
- `artifacts.sandbox=auto` prefiere Bubblewrap, luego una imagen local disponible
  mediante Podman/Docker y, si no existe, informa la degradación a subproceso acotado.
- `artifacts.sandbox=strict` falla cerrado sin Bubblewrap ni frontera OCI local.
  OCI usa `--pull=never`: analizar un archivo nunca descarga una imagen.
- JSON/NDJSON solo ocupan stdout; los logs estructurados van a stderr/archivos.
- NDJSON emite un envelope versionado por elemento de colección.
- los reportes seudonimizan por defecto; `--include-sensitive` es explícito.
- los perfiles proxy actualizan la política SQLite sin guardar credenciales.

`MORSA_REQUESTS_PER_SECOND`, `MORSA_SANDBOX` y `MORSA_PARSER_OCI_IMAGE` son
overrides por proceso; la imagen OCI indicada debe existir localmente. Las variables
proxy estándar y `NO_PROXY` conservan la precedencia documentada.

## Límites

Los valores inválidos fallan antes de ejecutar. Los techos principales incluyen 1.024
workers, 1.000 requests/s, 20 redirects, 2.047 MiB por descarga, 100 GiB de datos
descomprimidos, 100.000 resultados y presupuestos acotados de intentos/leases proxy.
