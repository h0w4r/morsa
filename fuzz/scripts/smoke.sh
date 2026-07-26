#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
DOTNET="$(resolve_dotnet)"

# El smoke reproduce una vez todos los seeds, sin mutarlos, bajo watchdog por proceso.
build_harness "${DOTNET}"
"${DOTNET}" "${FUZZ_DLL}" \
  --target all \
  --seed-only \
  --timeout-ms "${MORSA_FUZZ_TIMEOUT_MS:-2000}" \
  --max-input-bytes "${MORSA_FUZZ_MAX_INPUT_BYTES:-1048576}" \
  --max-total-seconds "${MORSA_FUZZ_TOTAL_SECONDS:-120}" \
  --stop-on-finding
