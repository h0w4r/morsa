# Scope and activity levels

Scope is a runtime authorization boundary, not a report filter. Morsa checks it
before active operations and again for each redirect or derived destination.

## Levels

| Mode | Typical operations | Network effect |
|---|---|---|
| `passive` | local parsing, imported datasets, provider indexes | no direct contact with target required |
| `active` | HTTP acquisition, DNS, TLS, bounded banner, crawler | direct target contact within budgets |
| `aggressive` | backup candidate validation and explicit fuzzing | higher request density, separately budgeted |

An entry's `MaximumMode` is a ceiling. A `passive` entry cannot be reused by an
active command; an `active` entry does not authorize aggressive work.

## Entry kinds

```bash
morsa scope add example.org --kind domain --max-mode active
morsa scope add api.example.org --kind host --max-mode active
morsa scope add https://example.org/public/ --kind url --max-mode active
morsa scope add 203.0.113.10 --kind ip --max-mode active
morsa scope add 203.0.113.0/28 --kind cidr --max-mode active
```

- `domain` includes the normalized domain according to the scope matcher; inspect
  `scope list` instead of assuming wildcard semantics.
- `host` authorizes that host identity, not arbitrary siblings.
- `url` is the narrowest web scope and should include scheme/path intentionally.
- `ip` and `cidr` apply to literal address operations.

## Redirect and SSRF rules

Every HTTP hop is canonicalized and checked. Userinfo is rejected, hostname and
port are normalized, private/link-local/loopback destinations are blocked unless
the project explicitly permits private networks, and redirections do not inherit
authorization solely from the original URL. DNS results are evaluated before
connection to reduce rebinding exposure.

Proxy selection never weakens scope. SOCKS5h can resolve remotely, but the logical
destination still passes scope and SSRF policy before a proxy tunnel is created.

## Recommended operating pattern

1. Initialize a separate workspace per authorization set.
2. Add the narrowest entries and lowest sufficient mode.
3. Run `scope list --json` and retain it with engagement evidence.
4. Run discovery/passive analysis first.
5. Elevate scope entries explicitly before active/aggressive phases.
6. Review `NetworkAttempt` and report coverage after execution.

## Rejection handling

A scope rejection returns the security exit class, records a diagnostic where an
operation already has a durable task, and sends no request. Do not work around a
rejection by adding a broad `0.0.0.0/0` entry; correct the intended target/mode.
