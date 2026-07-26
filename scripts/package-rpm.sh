#!/usr/bin/env bash
set -Eeuo pipefail

# Builds an RPM around a previously published glibc payload.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
version=""
rid=""

while (($#)); do
  case "$1" in
    --version) version="${2:?missing --version value}"; shift 2 ;;
    --rid) rid="${2:?missing --rid value}"; shift 2 ;;
    -h|--help) echo 'Usage: scripts/package-rpm.sh --version VERSION --rid linux-x64|linux-arm64'; exit 0 ;;
    *) printf 'error: unknown argument %s\n' "$1" >&2; exit 2 ;;
  esac
done

[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || {
  printf 'error: invalid semantic version\n' >&2; exit 2;
}
case "${rid}" in
  linux-x64) rpm_arch='x86_64' ;;
  linux-arm64) rpm_arch='aarch64' ;;
  *) printf 'error: RPM packages target glibc RIDs only\n' >&2; exit 2 ;;
esac

command -v rpmbuild >/dev/null 2>&1 || {
  printf 'error: rpmbuild is required\n' >&2; exit 3;
}

stage="${ROOT}/artifacts/stage/${rid}"
test -x "${stage}/bin/morsa" || {
  printf 'error: publish %s before packaging it\n' "${rid}" >&2; exit 3;
}

rpm_version="${version%%-*}"
if [[ "${version}" == *-* ]]; then
  rpm_release="0.1.${version#*-}"
else
  rpm_release="1"
fi
# RPM release identifiers cannot contain hyphens.
rpm_release="${rpm_release//-/.}"

topdir="${ROOT}/artifacts/package/rpm/${rid}"
rm -rf "${topdir}"
mkdir -p "${topdir}"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
spec="${topdir}/SPECS/morsa.spec"

sed \
  -e "s|@VERSION@|${rpm_version}|g" \
  -e "s|@RELEASE@|${rpm_release}|g" \
  -e "s|@STAGE@|${stage}|g" \
  "${ROOT}/packaging/rpm/morsa.spec.in" >"${spec}"

rpmbuild -bb "${spec}" \
  --target "${rpm_arch}" \
  --define "_topdir ${topdir}" \
  --define '_build_id_links none' \
  --define '__os_install_post %{nil}' \
  --define '__spec_install_post %{nil}'

rpm_file="$(find "${topdir}/RPMS" -type f -name '*.rpm' -print -quit)"
test -n "${rpm_file}"
output="${ROOT}/artifacts/dist/morsa-${version}-${rid}.rpm"
install -m 0644 "${rpm_file}" "${output}"
printf 'Created %s\n' "${output}"
