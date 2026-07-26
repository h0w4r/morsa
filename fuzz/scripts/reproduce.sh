#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 2 ]]; then
  printf 'Usage: %s <magic|zipxml|pdf|svg|rdp|ica|binary> <input>\n' "$0" >&2
  exit 2
fi

# shellcheck source=common.sh
source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
DOTNET="$(resolve_dotnet)"
TARGET="$1"
INPUT="$(realpath "$2")"

build_harness "${DOTNET}"
"${DOTNET}" "${FUZZ_DLL}" \
  --worker \
  --target "${TARGET}" \
  --input "${INPUT}" \
  --timeout-ms "${MORSA_FUZZ_TIMEOUT_MS:-2000}" \
  --max-input-bytes "${MORSA_FUZZ_MAX_INPUT_BYTES:-1048576}"
