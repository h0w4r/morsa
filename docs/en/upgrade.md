# Upgrade guide

## Principles

- Upgrade the executable and helpers as one coherent RID payload.
- Back up a quiescent workspace before the first start with a newer minor/major.
- Never copy only `morsa` while retaining older parser, plugin, or MCP helpers.
- Read release notes for schema, plugin API, proxy policy, and output changes.
- Test one copied workspace before rolling out to all investigations.

## Back up a workspace

Stop Morsa processes using that workspace. Then use SQLite's backup command:

```bash
sqlite3 /cases/acme/morsa.db ".backup '/backups/acme-before-morsa-1.0.db'"
tar -C /cases -czf /backups/acme-artifacts-before-morsa-1.0.tar.gz \
  acme/artifacts acme/morsa.toml acme/.morsa
```

If `sqlite3` is unavailable, copy `morsa.db`, `morsa.db-wal`, and `morsa.db-shm`
together only after all Morsa processes have stopped. Verify the backup hash.

## Upgrade a tar installation

```bash
sha256sum --check SHA256SUMS --ignore-missing
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo bash ./morsa-1.0.0-linux-x64/install.sh --prefix /usr/local
morsa version
morsa doctor --project /cases/acme
morsa project status --project /cases/acme --json
```

Installation replaces only product-owned files and rewrites the install manifest.
Workspace migration occurs when the new version initializes the store.

## DEB/RPM/OCI

```bash
sudo apt install ./morsa-1.0.0-linux-x64.deb
# or
sudo dnf upgrade ./morsa-1.0.0-linux-x64.rpm
```

For OCI, pin and test the new digest; do not rely on a mutable `latest` tag:

```bash
docker pull ghcr.io/h0w4r/morsa:v1.0.0
docker run --rm -v /cases/acme:/workspace \
  ghcr.io/h0w4r/morsa:v1.0.0 doctor
```

## From 0.x to 1.0

1. Export JSON and an evidence bundle with the old binary if it supports them.
2. Back up SQLite/artifacts/config/plugin catalog.
3. Install all 1.0 helpers together.
4. Run `doctor`, then `project status --json` without running active work.
5. Check scope normalization and `MaximumMode` values.
6. Inspect proxy pools: 1.0 never enables direct fallback silently and only resolves
   built-in secrets from `env:NAME`.
7. Reinstall/revalidate plugins with API version `1` and optional entry hash.
8. Run `analyze all` only if release notes require reanalysis; preserve old report.
9. Run `run resume` for durable pending work.
10. Compare entity/finding/report counts and review all diagnostics.

## Plugin compatibility

The external protocol is `morsa-plugin/1` and manifest `apiVersion` must be `1`.
Keep multiple plugin versions installed so `plugin rollback ID` remains possible.
After upgrading:

```bash
morsa plugin list --json
morsa plugin run example.reputation health --input '{}' --timeout 10
```

## Rollback

1. Stop all Morsa processes for the workspace.
2. Restore the previous product package or tar payload.
3. Restore the database and artifact/config/plugin backup as one set if the newer
   binary applied an incompatible migration.
4. Run the old `doctor` and `project status`.
5. Do not open a migrated database with an older binary unless release notes state
   that backward schema access is supported.

Keep the failed upgraded copy for diagnosis; do not overwrite the only evidence.
