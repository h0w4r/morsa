#!/usr/bin/env bash
set -Eeuo pipefail

# Combines the shipped payload with the dependency graphs embedded by single-file publish.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
rid="${1:?Usage: scripts/prepare-sbom-input.sh RID}"
case "${rid}" in
  linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64) ;;
  *) printf 'error: unsupported RID %s\n' "${rid}" >&2; exit 2 ;;
esac

stage="${ROOT}/artifacts/stage/${rid}"
input="${ROOT}/artifacts/sbom-input/${rid}"
test -x "${stage}/bin/morsa" || {
  printf 'error: missing staged payload %s\n' "${stage}" >&2; exit 3;
}

rm -rf "${input}"
mkdir -p "${input}/payload" "${input}/dependencies"
cp -a "${stage}/." "${input}/payload/"

dependency_count=0
while IFS= read -r -d '' dependency; do
  relative="${dependency#"${ROOT}"/}"
  install -D -m 0644 "${dependency}" "${input}/dependencies/${relative}"
  dependency_count=$((dependency_count + 1))
done < <(find "${ROOT}/src" -type f \
  -path "*/obj/Release/net10.0/${rid}/*.deps.json" -print0)

if ((dependency_count < 4)); then
  printf 'error: expected four RID dependency graphs, found %s\n' "${dependency_count}" >&2
  exit 4
fi

# Normalize metadata independently of the checkout filesystem semantics.
find "${input}" -type d -exec chmod 0755 {} +
find "${input}" -type f -exec chmod 0644 {} +
printf 'Prepared SBOM input for %s with %s dependency graphs.\n' "${rid}" "${dependency_count}"
