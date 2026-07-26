#!/usr/bin/env bash
set -Eeuo pipefail

# Packages the already published glibc payload without invoking maintainer scripts.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
version=""
rid=""

while (($#)); do
  case "$1" in
    --version) version="${2:?missing --version value}"; shift 2 ;;
    --rid) rid="${2:?missing --rid value}"; shift 2 ;;
    -h|--help) echo 'Usage: scripts/package-deb.sh --version VERSION --rid linux-x64|linux-arm64'; exit 0 ;;
    *) printf 'error: unknown argument %s\n' "$1" >&2; exit 2 ;;
  esac
done

[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || {
  printf 'error: invalid semantic version\n' >&2; exit 2;
}
case "${rid}" in
  linux-x64) deb_arch='amd64' ;;
  linux-arm64) deb_arch='arm64' ;;
  *) printf 'error: DEB packages target glibc RIDs only\n' >&2; exit 2 ;;
esac

command -v dpkg-deb >/dev/null 2>&1 || {
  printf 'error: dpkg-deb is required\n' >&2; exit 3;
}

stage="${ROOT}/artifacts/stage/${rid}"
test -x "${stage}/bin/morsa" || {
  printf 'error: publish %s before packaging it\n' "${rid}" >&2; exit 3;
}

# Debian sorts prereleases before the final release when '-' becomes '~'.
deb_version="${version/-/\~}"
# Installed-Size is expressed in KiB and prevents apt from reporting a misleading zero-byte footprint.
installed_size="$(du -sk --apparent-size "${stage}" | awk '{print $1}')"
package_root="${ROOT}/artifacts/package/deb/${rid}"
rm -rf "${package_root}"
mkdir -p "${package_root}/DEBIAN" "${package_root}/usr"
chmod 0755 "${package_root}/DEBIAN"
cp -a "${stage}/bin" "${stage}/libexec" "${stage}/share" "${package_root}/usr/"

cat >"${package_root}/DEBIAN/control" <<EOF
Package: morsa
Version: ${deb_version}
Section: utils
Priority: optional
Architecture: ${deb_arch}
Installed-Size: ${installed_size}
Maintainer: Morsa maintainers <security@users.noreply.github.com>
Depends: ca-certificates, libc6 (>= 2.31), libgcc-s1, libstdc++6, zlib1g, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, libssl3 | libssl3t64
Homepage: https://github.com/h0w4r/morsa
Vcs-Git: https://github.com/h0w4r/morsa.git
License: GPL-3.0-or-later
Description: Linux metadata, OSINT and authorized reconnaissance CLI
 Morsa extracts metadata, correlates evidence, performs scoped discovery and
 reconnaissance, and supports isolated parsers, plugins, MCP and proxy pools.
EOF
chmod 0644 "${package_root}/DEBIAN/control"

install -m 0755 "${ROOT}/packaging/deb/postinst" "${package_root}/DEBIAN/postinst"
install -m 0755 "${ROOT}/packaging/deb/prerm" "${package_root}/DEBIAN/prerm"

output="${ROOT}/artifacts/dist/morsa-${version}-${rid}.deb"
mkdir -p "$(dirname "${output}")"
dpkg-deb --root-owner-group --build "${package_root}" "${output}"
printf 'Created %s\n' "${output}"
