# Verificación de releases

Morsa publica evidencia independiente: `SHA256SUMS` para integridad, SBOM SPDX y
CycloneDX para contenido, attestations GitHub/Sigstore OIDC para procedencia, y
provenance/SBOM BuildKit en OCI.

```bash
sha256sum --check SHA256SUMS --ignore-missing
grep 'morsa-1.0.0-linux-x64.tar.gz$' SHA256SUMS
gh attestation verify morsa-1.0.0-linux-x64.tar.gz -R h0w4r/morsa

# Los paquetes nativos son sujetos atestados independientes del mismo stage del RID.
gh attestation verify morsa-1.0.0-linux-x64.deb -R h0w4r/morsa
gh attestation verify morsa-1.0.0-linux-x64.rpm -R h0w4r/morsa
```

`--ignore-missing` sirve si descargaste un solo RID, pero el archivo debe aparecer
en el manifest. La attestation debe identificar `h0w4r/morsa`, el workflow/ref
esperado y el mismo SHA-256; una firma válida de otro repo no sirve.

Para verificar el predicado SPDX:

```bash
gh attestation verify morsa-1.0.0-linux-x64.tar.gz \
  -R h0w4r/morsa \
  --predicate-type https://spdx.dev/Document/v2.3
```

Inspecciona el archivo antes de extraer:

```bash
tar -tzf morsa-1.0.0-linux-x64.tar.gz
```

Debe existir un único raíz, `bin/morsa`, tres helpers en `libexec/morsa`, licencia,
man y completions. Rechaza rutas absolutas, `..`, credenciales o devices.

Desde fuente:

```bash
bash scripts/verify-release.sh --directory ./artifacts/release --version 1.0.0
```

Valida cuatro RIDs, rutas, payload, nombres sensibles, JSON SBOM, hashes y smoke x64.

OCI:

```bash
docker buildx imagetools inspect ghcr.io/h0w4r/morsa:v1.0.0
gh attestation verify oci://ghcr.io/h0w4r/morsa:v1.0.0 -R h0w4r/morsa
```

Confirma `linux/amd64` y `linux/arm64` y fija el digest en producción. Ante fallo no
ejecutes el binario: conserva URL, archivo, hash, output de attest y release, luego
reporta por `SECURITY.md`.
