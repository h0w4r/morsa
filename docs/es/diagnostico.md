# Diagnóstico

Empieza por evidencia:

```bash
morsa version --json
morsa doctor --project /ruta/workspace --json
morsa project status --project /ruta/workspace --json
morsa run resume --project /ruta/workspace --json >result.json 2>diagnostic.log
```

## Arranque

- `Exec format error`: compara `uname -m` y `file morsa`; usa RID x64/ARM64 correcto.
- “not found” con archivo existente: usa musl en Alpine y glibc en las demás.
- Error ICU: instala `icu-libs`/`libicu`.
- TLS falla globalmente: actualiza `ca-certificates`; no desactives validación.
- Permission denied: recupera bit ejecutable y evita mount `noexec`.

## SQLite/workspace

Confirma `--project` y permisos; evita WAL sobre filesystem de red; conserva db,
wal y shm juntos; usa `run resume` en vez de editar `Task.Status`. Un estado parcial
es deliberado: revisa diagnóstico antes de reintentar.

## Alcance

```bash
morsa scope list --project /cases/acme --json
```

Compara destino normalizado, puerto/esquema/ruta, IP resuelta y modo. Redirects
requieren autorización; proxy/NO_PROXY no amplían alcance. Agrega la entrada estrecha.

## Proxies

```bash
morsa proxy status --project /cases/acme --pool egress --json
morsa proxy test egress --project /cases/acme --url https://example.org/
```

`407`: revisa variable de `secret_ref`, nunca credencial inline. Cooldown: respeta
outcome/`Retry-After`. `socks5` resuelve local; `socks5h` remoto. Si todos fallan
igual, investiga bloqueo transversal; no elimines límites finitos.

## Providers, parsers, plugins y MCP

`provider status` muestra cobertura parcial; `MORSA_SEARXNG_URL` debe apuntar a la
instancia. Límites ZIP/XML protegen y no deben desactivarse. Conserva SHA-256 del
original. Para plugins revisa API/kind/entry/hash/permisos, JSONL en stdout y logs
en stderr; timeout queda `PLUGIN_TIMEOUT`. MCP debe arrancar por ruta absoluta sin
wrapper que contamine stdout, con outputs bajo `reports/`; compara con CLI equivalente.

## Build/release

```bash
find scripts -name '*.sh' -print0 | xargs -0 -n1 bash -n
bash scripts/build-release.sh --version 1.0.0-test --rid linux-x64
```

Si restore se atasca usa `--disable-parallel`, verifica DNS/TLS de NuGet y conserva
el error real. No apagues el audit de vulnerabilidades para pintar verde un release.
