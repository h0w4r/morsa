# Contributing to Morsa / Contribuir a Morsa

Morsa accepts focused changes that preserve evidence traceability, bounded
execution, Linux portability, and versioned contracts. Discussion, commands,
logs, public contracts, and source identifiers are English; user documentation
must be updated in both English and Spanish.

## Development setup

```bash
git clone https://github.com/h0w4r/morsa.git
cd morsa
bash scripts/install-dotnet.sh
export PATH="$PWD/.dotnet:$PATH"
dotnet restore Morsa.slnx --disable-parallel
dotnet build Morsa.slnx -c Release --no-restore
dotnet test Morsa.slnx -c Release --no-build
```

The repository requires SDK `10.0.302`, C# 14, nullable reference types, warnings
as errors, and deterministic builds. Do not enable trimming: reflection, MCP, and
plugin contracts require explicit compatibility work before that can change.

## Change rules

1. Create a focused branch and avoid unrelated formatting churn.
2. Add comments where intent, security bounds, or protocol behavior is not obvious.
3. Add unit tests for pure rules and integration tests for SQLite, network,
   interruption, parser, plugin, or MCP behavior.
4. Use generated non-sensitive fixtures; never commit real customer evidence,
   credentials, HAR authorization headers, or private proxy lists.
5. Update JSON schema versions only for contract changes and document migration.
6. Update both `docs/en/` and `docs/es/` for user-visible behavior.
7. Run build, tests, formatting, and the applicable packaging smoke script.

## Architecture boundaries

- `Morsa.Domain` contains pure models and rules.
- `Morsa.Application` owns use cases, durable tasks, and contracts.
- `Morsa.Infrastructure` owns SQLite, filesystem, network, proxies, and tools.
- CLI and MCP are adapters over the same application services.
- Hostile parsers and plugins must not be moved into the CLI process for convenience.
- New capabilities belong in an existing module unless an ownership/deployment
  boundary justifies another project.

## Security-sensitive changes

Changes to scope matching, redirects, DNS, proxy authentication, secret resolution,
archive extraction, parser process limits, plugin permissions, or MCP path handling
must include adversarial tests. Document the failure mode and ensure a partial
failure cannot be reported as full success.

## Commit and pull-request format

Use a clear Spanish commit message for coherent packages, for example:

```text
feat: agrega exportación reproducible de evidencias
fix: evita fuga de autorización durante redirecciones
docs: documenta la rotación automática de proxies
```

Pull requests should state scope, design, tests, security effects, compatibility,
and rollback. The CI, Linux smoke, dependency review, and CodeQL gates must pass.

## Licensing and FOCA-derived work

Contributions are accepted under GPL-3.0-or-later. Any selective FOCA port must
record upstream repository, tag, commit, source path, source hash, resulting path,
and transformation notes in the provenance inventory. Do not copy dependencies
with incompatible or ambiguous licenses.

---

## Resumen en español

Los cambios deben ser pequeños, probados, comentados y portables a Linux glibc y
musl. Actualiza documentación EN/ES, conserva los límites de dominio/aplicación/
infraestructura, nunca introduzcas secretos ni evidencia real y no integres parsers
o plugins hostiles dentro del proceso principal. Toda modificación de alcance,
red, proxies, archivos, plugins o MCP requiere pruebas adversariales y un rollback
explicado en el PR.
