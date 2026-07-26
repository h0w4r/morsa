#!/usr/bin/env bash
set -Eeuo pipefail

# Emits a canonical GNU-compatible SHA-256 manifest for regular release files.
directory="${1:-artifacts/dist}"
output_name="${2:-SHA256SUMS}"
directory="$(cd "${directory}" && pwd)"
output="${directory}/${output_name}"
temporary="${output}.tmp"

command -v sha256sum >/dev/null 2>&1 || {
  printf 'error: sha256sum is required\n' >&2; exit 2;
}

(
  cd "${directory}"
  find . -maxdepth 1 -type f ! -name "${output_name}" ! -name "${output_name}.tmp" \
    -printf '%P\n' | LC_ALL=C sort | while IFS= read -r file; do
      [[ -n "${file}" ]] && sha256sum -- "${file}"
    done
) >"${temporary}"

test -s "${temporary}" || {
  rm -f "${temporary}"
  printf 'error: no release files found in %s\n' "${directory}" >&2
  exit 3
}
mv "${temporary}" "${output}"
printf 'Created %s\n' "${output}"
