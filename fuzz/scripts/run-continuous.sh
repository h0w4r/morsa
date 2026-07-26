#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CHUNK_SECONDS="${MORSA_FUZZ_CHUNK_SECONDS:-900}"
ROUND=0

# Cada ronda usa una semilla diferente, pero la imprime y preserva en cada finding para reproducibilidad.
while true; do
  ROUND=$((ROUND + 1))
  export MORSA_FUZZ_TOTAL_SECONDS="${CHUNK_SECONDS}"
  export MORSA_FUZZ_SEED="$((1297044051 + ROUND))"
  printf 'morsa-fuzz round=%s seed=%s target=%s\n' \
    "${ROUND}" "${MORSA_FUZZ_SEED}" "${MORSA_FUZZ_TARGET:-all}"
  "${SCRIPT_DIR}/run-bounded.sh" "$@"
done
