#!/usr/bin/env bash
set -Eeuo pipefail

# Installs the repository-pinned SDK without changing the machine-wide runtime.
readonly SDK_VERSION="10.0.302"
REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
INSTALL_DIRECTORY="${DOTNET_INSTALL_DIR:-${REPOSITORY_ROOT}/.dotnet}"
# A repository shared with Windows can already contain a native Windows host.
# Keep platform-native hosts separate because their SDK layout is not portable.
if [[ -z "${DOTNET_INSTALL_DIR:-}" && ! -x "${INSTALL_DIRECTORY}/dotnet" && \
      -f "${INSTALL_DIRECTORY}/dotnet.exe" ]]; then
  INSTALL_DIRECTORY="${REPOSITORY_ROOT}/.dotnet-linux"
fi
readonly INSTALL_DIRECTORY
readonly INSTALL_SCRIPT="${TMPDIR:-/tmp}/morsa-dotnet-install-${SDK_VERSION}.sh"

installed_version() {
  # Avoid a parent global.json selecting another repository-local SDK.
  (cd / && "$1" --version)
}

if [[ -x "${INSTALL_DIRECTORY}/dotnet" ]] && \
   [[ "$(installed_version "${INSTALL_DIRECTORY}/dotnet")" == "${SDK_VERSION}" ]]; then
  printf 'Morsa .NET SDK %s is already installed in %s\n' "${SDK_VERSION}" "${INSTALL_DIRECTORY}"
  exit 0
fi

command -v curl >/dev/null 2>&1 || {
  printf 'error: curl is required to download the official dotnet-install script\n' >&2
  exit 2
}

mkdir -p "${INSTALL_DIRECTORY}"
curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 \
  'https://dot.net/v1/dotnet-install.sh' -o "${INSTALL_SCRIPT}"

# The installer is executed only after download over validated HTTPS.
bash "${INSTALL_SCRIPT}" \
  --version "${SDK_VERSION}" \
  --install-dir "${INSTALL_DIRECTORY}" \
  --no-path
rm -f "${INSTALL_SCRIPT}"

actual_version="$(installed_version "${INSTALL_DIRECTORY}/dotnet")"
if [[ "${actual_version}" != "${SDK_VERSION}" ]]; then
  printf 'error: expected SDK %s but installed %s\n' "${SDK_VERSION}" "${actual_version}" >&2
  exit 3
fi

printf 'Installed .NET SDK %s in %s\n' "${actual_version}" "${INSTALL_DIRECTORY}"
printf 'Run: export PATH="%s:%s"\n' "${INSTALL_DIRECTORY}" "\$PATH"
