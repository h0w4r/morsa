#!/usr/bin/env bash
set -euo pipefail

# Resuelve el repositorio incluso cuando el script se invoca desde otra carpeta.
FUZZ_SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MORSA_ROOT="$(cd -- "${FUZZ_SCRIPT_DIR}/../.." && pwd)"
FUZZ_PROJECT="${MORSA_ROOT}/fuzz/Morsa.FuzzHarness/Morsa.FuzzHarness.csproj"
# shellcheck disable=SC2034 # Consumed by the scripts that source this shared harness.
FUZZ_DLL="${MORSA_ROOT}/fuzz/Morsa.FuzzHarness/bin/Release/net10.0/Morsa.FuzzHarness.dll"

resolve_dotnet() {
  if [[ -n "${DOTNET_HOST_PATH:-}" && -x "${DOTNET_HOST_PATH}" ]]; then
    printf '%s\n' "${DOTNET_HOST_PATH}"
    return
  fi

  if [[ -n "${DOTNET_ROOT:-}" && -x "${DOTNET_ROOT}/dotnet" ]]; then
    printf '%s\n' "${DOTNET_ROOT}/dotnet"
    return
  fi

  command -v dotnet || {
    printf 'dotnet 10 SDK/runtime was not found in PATH.\n' >&2
    exit 127
  }
}

build_harness() {
  local dotnet="$1"
  "${dotnet}" build "${FUZZ_PROJECT}" --configuration Release --verbosity minimal
}
