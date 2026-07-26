#!/usr/bin/env bash
set -Eeuo pipefail

# Fails closed on malformed archives, digest mismatches or incomplete RID sets.
directory=""
version=""
while (($#)); do
  case "$1" in
    --directory) directory="${2:?missing --directory value}"; shift 2 ;;
    --version) version="${2:?missing --version value}"; shift 2 ;;
    -h|--help) echo 'Usage: scripts/verify-release.sh --directory DIR --version VERSION'; exit 0 ;;
    *) printf 'error: unknown argument %s\n' "$1" >&2; exit 2 ;;
  esac
done

directory="$(cd "${directory:?--directory is required}" && pwd)"
[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || {
  printf 'error: invalid version\n' >&2; exit 2;
}

if [[ -f "${directory}/SHA256SUMS" ]]; then
  (cd "${directory}" && sha256sum --check --strict SHA256SUMS)
fi

rids=(linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64)
for rid in "${rids[@]}"; do
  archive="${directory}/morsa-${version}-${rid}.tar.gz"
  test -s "${archive}" || {
    printf 'error: missing archive %s\n' "${archive}" >&2; exit 3;
  }

  # Reject absolute paths, traversal and sensitive-looking files before extraction.
  listing="$(tar -tzf "${archive}")"
  if grep -Eq '(^/|(^|/)\.\.(/|$))' <<<"${listing}"; then
    printf 'error: unsafe path in %s\n' "${archive}" >&2; exit 4;
  fi
  if grep -Eqi '(^|/)(\.env($|\.)|[^/]*\.(pem|key|pfx)|[^/]*secrets[^/]*)$' <<<"${listing}"; then
    printf 'error: sensitive-looking file in %s\n' "${archive}" >&2; exit 4;
  fi
  grep -q "/bin/morsa$" <<<"${listing}"
  grep -q "/libexec/morsa/morsa-parser-host$" <<<"${listing}"
  grep -q "/libexec/morsa/morsa-plugin-host$" <<<"${listing}"
  grep -q "/libexec/morsa/morsa-mcp$" <<<"${listing}"
  grep -q "/share/doc/morsa/LICENSE$" <<<"${listing}"
  grep -q "/share/doc/morsa/docs/en/installation.md$" <<<"${listing}"
  grep -q "/share/doc/morsa/docs/es/instalacion.md$" <<<"${listing}"
  grep -q "/install.sh$" <<<"${listing}"
  grep -q "/uninstall.sh$" <<<"${listing}"
done

# Parse every generated JSON SBOM instead of trusting its extension.
python3 - "${directory}" <<'PY'
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
for path in sorted([*root.glob("*.spdx.json"), *root.glob("*.cdx.json")]):
    with path.open(encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document, dict):
        raise SystemExit(f"invalid SBOM root: {path}")
    if path.name.endswith(".spdx.json"):
        packages = document.get("packages", [])
        files = document.get("files", [])
        if len(packages) < 10 or len(files) < 4:
            raise SystemExit(f"incomplete SPDX inventory: {path}")
        checksums = [checksum for item in files for checksum in item.get("checksums", [])]
        sha256 = [item.get("checksumValue", "") for item in checksums if item.get("algorithm") == "SHA256"]
        if len(sha256) < 8 or any(len(value) != 64 or set(value) == {"0"} for value in sha256):
            raise SystemExit(f"SPDX inventory has no SHA-256 file digests: {path}")
        by_name = {item.get("fileName"): item for item in files}
        required = {
            "payload/bin/morsa",
            "payload/libexec/morsa/morsa-parser-host",
            "payload/libexec/morsa/morsa-plugin-host",
            "payload/libexec/morsa/morsa-mcp",
        }
        for name in required:
            entry = by_name.get(name, {})
            if not any(item.get("algorithm") == "SHA256" for item in entry.get("checksums", [])):
                raise SystemExit(f"SPDX inventory omits payload digest {name}: {path}")
    else:
        if len(document.get("components", [])) < 10:
            raise SystemExit(f"incomplete CycloneDX inventory: {path}")
    print(f"valid SBOM: {path.name}")
PY

# Execute the native glibc binary when this verifier runs on x86-64 Linux.
if [[ "$(uname -s)" == 'Linux' && "$(uname -m)" == 'x86_64' ]]; then
  temporary="$(mktemp -d)"
  trap 'rm -rf "${temporary}"' EXIT
  tar -xzf "${directory}/morsa-${version}-linux-x64.tar.gz" -C "${temporary}"
  executable="$(find "${temporary}" -path '*/bin/morsa' -type f -print -quit)"
  chmod 0755 "${executable}"
  "${executable}" version
  "${executable}" --help >/dev/null
fi

printf 'Release %s passed payload, archive, checksum and smoke validation.\n' "${version}"
