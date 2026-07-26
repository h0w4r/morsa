# Security Policy / Política de seguridad

## Supported versions / Versiones soportadas

| Version | Security fixes |
|---|---|
| Latest `1.x` | Yes |
| Prerelease used to prepare latest `1.x` | Best effort |
| `< 1.0` | No |

## Reporting a vulnerability / Reportar una vulnerabilidad

Do not open a public issue with exploit details, tokens, proxy credentials,
workspace databases, or sample evidence containing personal data. Use
[GitHub private vulnerability reporting](https://github.com/h0w4r/morsa/security/advisories/new).

No abras un issue público con detalles de explotación, tokens, credenciales de
proxy, bases SQLite del workspace ni evidencia con datos personales. Usa el
[reporte privado de vulnerabilidades](https://github.com/h0w4r/morsa/security/advisories/new).

Include / Incluye:

- affected Morsa version, RID, distribution, architecture, and installation type;
- affected command, module, parser, provider, proxy protocol, or plugin boundary;
- minimal reproduction using non-sensitive fixtures;
- expected versus observed behavior and relevant stderr diagnostics;
- impact assessment and whether a public proof of concept already exists;
- proposed fix, if available.

## Response targets / Objetivos de respuesta

| Stage | Target |
|---|---|
| Initial acknowledgement | 3 business days |
| Reproduction or request for evidence | 7 business days |
| Severity and remediation plan | 14 business days |
| Coordinated disclosure | Agreed per report; normally at most 90 days |

These are operational targets, not a service-level agreement. Reporters receive
credit unless they request anonymity or the report is not original.

## Security invariants / Invariantes

- Scope and SSRF validation run before every request and redirect.
- Proxy credentials are resolved from `secret_ref`; they are not persisted in
  SQLite, logs, reports, or evidence bundles.
- `Proxy-Authorization` must never reach an origin server.
- Cookie containers and network handlers are isolated by proxy identity/session.
- Direct network fallback is explicit; it is never silently enabled.
- Parser, plugin, and MCP stdout boundaries are treated as hostile protocols.
- XML DTD/entity expansion, archive traversal, oversized responses, and unbounded
  retries fail closed.
- Release artifacts require checksums, two SBOM formats, and build provenance.

See the complete [threat model](docs/en/threat-model.md) or
[modelo de amenazas](docs/es/modelo-amenazas.md).

## Secrets accidentally committed

Revoke or rotate the secret first. Removing a Git blob does not revoke a token.
Then contact maintainers through the private advisory. Do not paste the secret
again. Workspace fixtures accepted in reports must be newly generated and scrubbed.

Primero revoca o rota el secreto. Luego contacta mediante el advisory privado;
no vuelvas a pegar el valor. Las fixtures deben ser nuevas y estar redactadas.

## Out of scope

- Availability attacks against public project infrastructure.
- Vulnerabilities that exist only after deliberately disabling TLS, scope, sandbox,
  redaction, or proxy fallback controls and are clearly documented as such.
- Bugs in external search engines or proxy providers that Morsa cannot remediate.
- Reports based solely on automated version matching without a working impact path.
