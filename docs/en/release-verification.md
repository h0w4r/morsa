# Release verification

Morsa publishes independent evidence for integrity, contents, and provenance:

1. `SHA256SUMS` detects byte changes.
2. SPDX and CycloneDX JSON inventory packaged components.
3. GitHub artifact attestations bind digests to the repository/workflow using a
   short-lived Sigstore certificate issued through OIDC.
4. OCI manifests include BuildKit provenance and SBOM attestations.

## Verify downloaded files

```bash
sha256sum --check SHA256SUMS --ignore-missing
```

The expected line has the exact file name and a 64-character digest. `--ignore-missing`
is appropriate when only one of the four RIDs was downloaded; it is not permission
to accept a file absent from the manifest. Confirm it appears:

```bash
grep 'morsa-1.0.0-linux-x64.tar.gz$' SHA256SUMS
```

## Verify GitHub provenance

Install a current GitHub CLI and authenticate if required by its attestation API:

```bash
gh attestation verify morsa-1.0.0-linux-x64.tar.gz \
  --repo h0w4r/morsa

# Native packages are independent attested subjects built from the same RID stage.
gh attestation verify morsa-1.0.0-linux-x64.deb --repo h0w4r/morsa
gh attestation verify morsa-1.0.0-linux-x64.rpm --repo h0w4r/morsa
```

Inspect JSON when validating policy automation:

```bash
gh attestation verify morsa-1.0.0-linux-x64.tar.gz \
  --repo h0w4r/morsa --format json > verification.json
jq '.[].verificationResult.statement.subject' verification.json
```

Verification must identify `h0w4r/morsa`, the expected release workflow/ref, and
the same SHA-256. A valid signature from another repository is not valid for Morsa.

## Verify an SBOM attestation

```bash
gh attestation verify morsa-1.0.0-linux-x64.tar.gz \
  -R h0w4r/morsa \
  --predicate-type https://spdx.dev/Document/v2.3
```

Then inspect the downloaded `*.spdx.json` and `*.cdx.json` with your policy tool.
The two formats are generated from the staged, self-contained payload for that RID.

## Inspect an archive without extracting

```bash
tar -tzf morsa-1.0.0-linux-x64.tar.gz
```

It must contain one top-level `morsa-1.0.0-linux-x64/` directory, `bin/morsa`,
three `libexec/morsa/` helpers, license/notice, man page, and completions. Reject
absolute paths, `..` traversal, credential files, or unexpected device nodes.

## Verify locally from the source tree

```bash
bash scripts/verify-release.sh \
  --directory ./artifacts/release \
  --version 1.0.0
```

The verifier checks all four RIDs, archive paths, required payload, sensitive file
names, JSON SBOM syntax, checksums, and an x64 native `version`/`--help` smoke.

## OCI

```bash
docker buildx imagetools inspect ghcr.io/h0w4r/morsa:v1.0.0
gh attestation verify oci://ghcr.io/h0w4r/morsa:v1.0.0 \
  -R h0w4r/morsa
```

Confirm both `linux/amd64` and `linux/arm64`. Pin the digest in production:

```text
ghcr.io/h0w4r/morsa@sha256:EXPECTED_MANIFEST_DIGEST
```

## Failure response

Do not run the binary. Preserve the URL, file, digest output, attestation output,
and release name; report through the private channel in `SECURITY.md`. Re-download
from the canonical GitHub release before assuming a transient mirror error.
