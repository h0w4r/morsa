#!/usr/bin/env bash
set -Eeuo pipefail

# Generates both required SBOM dialects using Syft's filesystem cataloger.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
version="${1:?Usage: scripts/generate-sboms.sh VERSION RID}"
rid="${2:?Usage: scripts/generate-sboms.sh VERSION RID}"
stage="${ROOT}/artifacts/stage/${rid}"
input="${ROOT}/artifacts/sbom-input/${rid}"
dist="${ROOT}/artifacts/dist"

command -v syft >/dev/null 2>&1 || {
  printf 'error: syft is required; see https://github.com/anchore/syft\n' >&2
  exit 2
}
test -d "${stage}" || {
  printf 'error: missing payload %s\n' "${stage}" >&2; exit 3;
}
mkdir -p "${dist}"

bash "${ROOT}/scripts/prepare-sbom-input.sh" "${rid}"

syft --config "${ROOT}/packaging/sbom/syft.yaml" "dir:${input}" \
  -o "spdx-json=${dist}/morsa-${version}-${rid}.spdx.json"
syft --config "${ROOT}/packaging/sbom/syft.yaml" "dir:${input}" \
  -o "cyclonedx-json=${dist}/morsa-${version}-${rid}.cdx.json"

printf 'Generated SPDX and CycloneDX SBOMs for %s\n' "${rid}"
