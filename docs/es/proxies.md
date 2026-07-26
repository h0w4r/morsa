# Pools de proxies y rotación automática

Morsa trata cada proxy como identidad de red con salud, concurrencia, DNS,
credenciales, cookies y leases. Tener cien proxies no multiplica cien veces el
rate limit: el limitador del objetivo sigue siendo global.

| Esquema | Transporte | DNS |
|---|---|---|
| `http://host:port` | forward/CONNECT | local |
| `https://host:port` | CONNECT protegido con TLS | local |
| `socks4://host:port` | SOCKS4 TCP | local |
| `socks5://host:port` | SOCKS5 TCP | local |
| `socks5h://host:port` | SOCKS5 TCP | remoto |

Las credenciales inline se rechazan; usa `secret_ref`.

```bash
morsa proxy pool add investigacion \
  --policy sticky --max-rotations 5 --max-attempts 8
morsa proxy import ./proxies.ndjson --pool investigacion
morsa proxy status --pool investigacion
```

TXT usa una URI por línea. CSV: `uri,secret_ref,weight`. JSON es un arreglo y
NDJSON un objeto por línea:

```json
{"uri":"socks5h://proxy.example:1080","secret_ref":"env:MORSA_PROXY_A","weight":5,"tags":["region:pe"]}
```

```bash
export MORSA_PROXY_A='usuario:clave'
```

El resolver integrado solo acepta `env:NAME`; el valor jamás entra en SQLite.

## Fuentes

`proxy import` acepta TXT/CSV/JSON/NDJSON, `-` para stdin, `env`, URL HTTPS y
`command:/ruta/ejecutable`. La fuente remota no sigue redirects, limita conexión
a 10 s, total a 20 s y cuerpo a 2 MiB. El comando externo no usa shell, emite
texto/JSONL y vence a los 30 s. Se reconocen `HTTP_PROXY`, `HTTPS_PROXY`,
`ALL_PROXY`, `NO_PROXY` y variantes minúsculas.

`morsa proxy source list` enumera adaptadores y disponibilidad del entorno;
`morsa proxy source load SOURCE --pool NOMBRE` es el alias explícito de importación.
Los perfiles TOML se aplican idempotentemente al iniciar el workspace:

```toml
[proxy_profiles.investigacion]
policy = "least-latency"
max_rotations = 5
max_attempts = 8
cooldown_seconds = 120
lease_ttl_seconds = 900
allow_direct_fallback = false
```

## Políticas

| Política | Comportamiento |
|---|---|
| `sticky` | conserva endpoint por sesión hasta fallo/expiración |
| `round-robin` | alterna elegibles en orden |
| `random` | elige aleatoriamente |
| `weighted` | pondera por `ProxyEndpoint.Weight` |
| `least-latency` | usa menor latencia EWMA |
| `failover` | mantiene orden primario/secundario |

Se excluyen endpoints disabled/quarantined/unavailable/cooldown y se respeta
`ProxyEndpoint.MaxConcurrency`.

## Rotación

Se clasifican fallos DNS/connect/TLS, timeout, `407`, `403`, `429`, `5xx`
configurado, challenge, cancelación y error desconocido. Los reintentables rotan
dentro de `ProxyPool.MaxRotations` y `ProxyPool.MaxAttempts`. `Retry-After` afecta
cooldown; fallos consecutivos, contadores, EWMA, estado y samples quedan persistidos.
No existen bucles infinitos. Un bloqueo transversal abre circuito y el trabajo
pendiente puede continuar con otro pool.

`ProxyLease` vincula run/task/session/endpoint. Cookies y handlers no se comparten
entre identidades. `Proxy-Authorization` no llega al origen. TLS sigue validado,
el fallback directo requiere `AllowDirectFallback=true`, y alcance/SSRF se ejecutan
antes de cada intento y redirect.

```bash
morsa proxy test investigacion --url https://example.org/
morsa proxy status --pool investigacion --json
morsa proxy reset --pool investigacion
```

Los reportes muestran cobertura, fallos, rotaciones, cooldown y fallback directo.
