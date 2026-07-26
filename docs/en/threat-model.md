# Threat model

## Assets

- authorization scope and activity ceilings;
- original artifacts and their SHA-256 identity;
- evidence provenance, findings, entities, and relationships;
- workspace SQLite journal and reproducible reports;
- proxy credentials, cookies, provider API secrets, and plugin secrets;
- operator host, network identity, and release consumers.

## Adversaries

1. A malicious remote origin controlling DNS, redirects, TLS, headers, and bodies.
2. A malicious or compromised proxy observing/tampering with traffic.
3. A hostile artifact exploiting archive/XML/image/PDF/OLE parsers.
4. A malicious plugin package or external executable.
5. A local unprivileged user racing workspace files or reading permissive output.
6. A compromised dependency, build action, base image, or release mirror.
7. Operator error that configures scope, fallback, upload, or redaction incorrectly.

## Trust boundaries and controls

| Boundary | Representative threats | Controls | Verification |
|---|---|---|---|
| URL/DNS to socket | SSRF, rebinding, redirect escape | canonicalization, scope per hop, private-range policy, budgets | SSRF/redirect/rebinding integration tests |
| Proxy | credential leak, origin header leak, cookie crossover | `secret_ref`, separate handlers/cookies, CONNECT auth isolation, TLS validation | mock origin/proxy capture tests |
| Rotation | infinite retry, rate multiplication, transversal block | max attempts/rotations, global target limiter, cooldown, circuit | timeout/403/429/challenge tests |
| Archive/XML | traversal, ZIP bomb, XXE | safe path checks, entry/byte budgets, DTD prohibited, null resolver | adversarial corpus/fuzzing |
| Parser process | crash, hang, memory/CPU exhaustion | separate host, timeout, sandbox auto/strict, bounded protocol | crash/hang/limit tests |
| Plugin ZIP | traversal, hash substitution | staged safe extraction, manifest validation, optional SHA-256 | package tests |
| Plugin process | hang, stdout flood, env theft | no shell, clean env, timeout, message/stderr caps | malformed/hanging plugin tests |
| MCP | stdout corruption, path escape, unbounded pages | stderr logs, canonical workspace, reports confinement, input bounds | protocol/path tests |
| SQLite | partial commit, false success | transactions, WAL, durable task state, explicit partial status | interruption/resume tests |
| Reports | secret/PII disclosure, nondeterminism | URI/secret redaction, explicit policy, stable order/timestamps | snapshot/reproducibility tests |
| Release | tampering, dependency confusion | central packages, NuGet audit, CodeQL, checksums, SBOM, OIDC attestations | clean install and attestation verification |

## Network invariants

- Every attempt and redirect is in scope before bytes leave the host.
- Proxy count never multiplies the target's request budget.
- No `Proxy-Authorization` header is sent after the proxy boundary.
- SOCKS5h remote DNS is explicit in `ProxyEndpoint.DnsMode`.
- TLS certificate validation is on by default.
- Direct fallback is recorded and requires explicit policy.
- Retry and rotation loops have independent finite ceilings.

## Artifact invariants

- Content identity is SHA-256, not filename or extension.
- Magic/MIME classification precedes extractor selection.
- Parsers do not execute embedded macros, scripts, links, or objects.
- Decompression and parser byte/entry/time budgets fail closed.
- One malformed artifact cannot change another artifact's result.
- Evidence locators trace observations to source structures.

## Plugin and external-tool limitations

Permission declarations are validated and logged but do not replace kernel-level
isolation. `sandbox=auto` prefers Bubblewrap, then a locally cached pull-free OCI
boundary, and reports any degradation; `sandbox=strict` refuses execution without
either isolation. OCI runs networkless, read-only, capability-free and resource-bound.
Tools such as YARA and ClamAV run locally; reputation uploads must be separate,
explicit plugin operations.

## Residual risk

- A local user with read access to a workspace can read unredacted evidence.
- A proxy can observe plaintext destinations/protocol data not protected end-to-end.
- Remote DNS through a proxy relies on that proxy's resolver and integrity.
- Native parsing dependencies can contain unknown memory-safety bugs.
- Declared plugin permissions are not a complete mandatory-access-control system
  on every supported distribution.
- Search-provider terms, availability, and response formats can change.

## Safe defaults and opt-ins

Private-network access, aggressive activity, direct fallback, provider credentials,
sample upload, and strict sandbox degradation are never silent defaults. Reports
must make effective scope, proxy route, fallback, partial failure, and redaction
visible to the operator.
