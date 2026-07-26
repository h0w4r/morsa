#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"
DOTNET="$(resolve_dotnet)"
TARGET="${MORSA_FUZZ_TARGET:-all}"
ITERATIONS="${MORSA_FUZZ_ITERATIONS:-10000}"
TIMEOUT_MS="${MORSA_FUZZ_TIMEOUT_MS:-2000}"
TOTAL_SECONDS="${MORSA_FUZZ_TOTAL_SECONDS:-900}"
MAX_INPUT_BYTES="${MORSA_FUZZ_MAX_INPUT_BYTES:-1048576}"
MEMORY_MB="${MORSA_FUZZ_MEMORY_MB:-1536}"
SEED="${MORSA_FUZZ_SEED:-1297044051}"

build_harness "${DOTNET}"

# ulimit protege al controlador y a todos sus workers; cada worker además tiene watchdog propio.
ulimit -v "$((MEMORY_MB * 1024))" 2>/dev/null || true
ulimit -t "$((TOTAL_SECONDS + 60))" 2>/dev/null || true

COMMAND=(
  "${DOTNET}" "${FUZZ_DLL}"
  --target "${TARGET}"
  --iterations "${ITERATIONS}"
  --timeout-ms "${TIMEOUT_MS}"
  --max-input-bytes "${MAX_INPUT_BYTES}"
  --max-total-seconds "${TOTAL_SECONDS}"
  --seed "${SEED}"
  --stop-on-finding
)

if command -v timeout >/dev/null 2>&1; then
  timeout --signal=TERM --kill-after=10s "$((TOTAL_SECONDS + 30))s" "${COMMAND[@]}" "$@"
else
  "${COMMAND[@]}" "$@"
fi
