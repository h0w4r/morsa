# Proxy pools and automatic rotation

Morsa treats a proxy as a scoped network identity with health, concurrency, DNS,
credential, cookie, and lease state. A large endpoint list is not permission to
multiply the target request rate: the target limiter remains global.

## Supported protocols

| URI scheme | Transport | DNS mode |
|---|---|---|
| `http://host:port` | HTTP forward / CONNECT as required | local |
| `https://host:port` | TLS-protected CONNECT proxy | local |
| `socks4://host:port` | SOCKS4 TCP | local |
| `socks5://host:port` | SOCKS5 TCP | local |
| `socks5h://host:port` | SOCKS5 TCP | remote hostname resolution |

Inline URI credentials are rejected. Use `secret_ref`.

## Create and load a pool

```bash
morsa proxy pool add research \
  --policy sticky --max-rotations 5 --max-attempts 8
morsa proxy import ./proxies.ndjson --pool research
morsa proxy status --pool research
```

Text files contain one URI per line. CSV columns are `uri,secret_ref,weight`.
JSON is an array and NDJSON is one object per line:

```json
{
  "uri": "socks5h://proxy.example:1080",
  "secret_ref": "env:MORSA_PROXY_RESEARCH",
  "weight": 5,
  "tags": ["provider:example", "region:pe"]
}
```

```bash
export MORSA_PROXY_RESEARCH='username:password'
```

Only `env:NAME` secret references are resolved by the built-in resolver. The
value is read on demand and is never stored in SQLite.

## Source types

`morsa proxy source list` reports every adapter and whether proxy environment
variables are present. `morsa proxy source load` is the explicit source-oriented
alias of `proxy import`:

```bash
morsa proxy source list --json
morsa proxy source load ./list.ndjson --pool research
morsa proxy import ./list.txt --pool research
morsa proxy import ./list.csv --pool research
morsa proxy import ./list.json --pool research
morsa proxy import ./list.ndjson --pool research
cat list.ndjson | morsa proxy import - --pool research
morsa proxy import env --pool research
morsa proxy import https://config.example/proxies.ndjson --pool research
morsa proxy import command:/opt/provider/export-proxies --pool research
```

Remote sources must be HTTPS, do not follow redirects, have 10-second connect and
20-second overall bounds, and stop after 2 MiB. External commands are executed
without a shell, must output text/JSONL, and time out after 30 seconds.

`HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`, and lowercase variants are recognized.
`NO_PROXY`/`no_proxy` matches exact hosts or domain suffixes and takes precedence.

Project/global `morsa.toml` profiles are applied idempotently at workspace startup:

```toml
[proxy_profiles.research]
policy = "least-latency"
max_rotations = 5
max_attempts = 8
cooldown_seconds = 120
lease_ttl_seconds = 900
allow_direct_fallback = false
```

## Selection policies

| Policy | Behavior | Good for |
|---|---|---|
| `sticky` | keeps endpoint for a session key until failure/expiry | cookies and provider sessions |
| `round-robin` | cycles eligible endpoints deterministically | even distribution |
| `random` | random eligible endpoint | reducing synchronized patterns |
| `weighted` | samples by positive endpoint weight | unequal provider capacity |
| `least-latency` | selects lowest EWMA latency | interactive acquisition |
| `failover` | preserves configured order until failure | primary/secondary routes |

Eligibility excludes disabled, quarantined, unavailable, and active-cooldown
endpoints, and enforces `ProxyEndpoint.MaxConcurrency`.

## Rotation and cooldown

The rotating client can classify DNS/connect/TLS failure, timeout, `407`, `403`,
`429`, configured `5xx`, challenge response, cancellation, and unknown failure.
Timeouts and retryable status responses rotate immediately within both
`ProxyPool.MaxRotations` and `ProxyPool.MaxAttempts`. `Retry-After` influences
cooldown; an endpoint's consecutive failures, counters, EWMA latency, status, and
health samples persist for diagnosis.

No loop is infinite. A provider-wide circuit can stop futile rotation when the
same block affects all identities. Pending work remains durable for another pool.

## Identity isolation

- A `ProxyLease` binds run/task/session key, endpoint, acquisition, expiry, release.
- Sticky cookie containers and connection handlers are never shared across proxy
  identities.
- `Proxy-Authorization` is consumed by proxy negotiation and stripped from origin
  requests and redirects.
- TLS validation stays enabled.
- Direct fallback occurs only when `AllowDirectFallback` is explicitly true.
- Destination scope and SSRF checks occur before every attempt and redirect.

## Operations

```bash
morsa proxy test research --url https://example.org/
morsa proxy status --pool research --json
morsa proxy reset --pool research
```

`reset` clears transient health; it does not invent successful samples or enable
disabled endpoints. Reports include proxy coverage, failures, rotations, cooldown,
and whether direct fallback was used.
