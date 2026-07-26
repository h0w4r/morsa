#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
FUZZ_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
cd "${FUZZ_ROOT}"

# El manifiesto evita que una modificación accidental del corpus cambie una campaña silenciosamente.
sha256sum --check SHA256SUMS
