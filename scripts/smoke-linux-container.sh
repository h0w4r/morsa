#!/usr/bin/env bash
set -Eeuo pipefail

# Runs one published RID in a completely clean Linux distribution container.
image="${MORSA_SMOKE_IMAGE:?MORSA_SMOKE_IMAGE is required}"
platform="${MORSA_SMOKE_PLATFORM:-linux/amd64}"
family="${MORSA_SMOKE_FAMILY:?MORSA_SMOKE_FAMILY is required}"
payload="$(cd "${MORSA_SMOKE_PAYLOAD:?MORSA_SMOKE_PAYLOAD is required}" && pwd)"
container_engine="${MORSA_CONTAINER_ENGINE:-docker}"
test -x "${payload}/bin/morsa" || chmod 0755 "${payload}/bin/morsa"
command -v "${container_engine}" >/dev/null 2>&1 || {
  printf 'error: container engine %s was not found\n' "${container_engine}" >&2; exit 2;
}
# Podman intentionally rejects ambiguous names that contain a namespace but no
# registry; Docker Hub is explicit here so the same matrix works in both engines.
if [[ "${container_engine##*/}" == 'podman' && "${image%%/*}" != *.* && \
      "${image}" == */* ]]; then
  image="docker.io/${image}"
fi

case "${family}" in
  apt) prepare='apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates libicu-dev libssl-dev libstdc++6 zlib1g' ;;
  dnf) prepare='dnf install -y ca-certificates libicu openssl-libs libstdc++ zlib && dnf clean all' ;;
  pacman) prepare='pacman -Syu --noconfirm --needed ca-certificates icu openssl gcc-libs zlib' ;;
  apk) prepare='apk add --no-cache ca-certificates icu-libs libssl3 libstdc++ zlib' ;;
  *) printf 'error: unsupported package family %s\n' "${family}" >&2; exit 2 ;;
esac

"${container_engine}" run --rm --platform "${platform}" \
  --network bridge \
  --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --tmpfs /root:rw,noexec,nosuid,size=16m \
  --volume "${payload}:/opt/morsa:ro" \
  "${image}" /bin/sh -lc \
  "${prepare} >/dev/null && \
   /opt/morsa/bin/morsa version && /opt/morsa/bin/morsa --help >/dev/null && \
   /bin/sh /opt/morsa/install.sh --prefix /usr/local && \
   /usr/local/bin/morsa version && \
   /bin/sh /usr/local/share/morsa/uninstall.sh --prefix /usr/local && \
   test ! -e /usr/local/bin/morsa"
