#!/bin/sh
set -eu

# Installs an extracted Morsa tar payload. Verify SHA256SUMS before invoking it.
prefix='/usr/local'
destdir=''
script_directory="$(cd "$(dirname "$0")" && pwd)"
if [ -x "${script_directory}/bin/morsa" ]; then
  payload="${script_directory}"
else
  payload="$(cd "${script_directory}/.." && pwd)"
fi

while [ "$#" -gt 0 ]; do
  case "$1" in
    --prefix) prefix="${2:?missing --prefix value}"; shift 2 ;;
    --destdir) destdir="${2:?missing --destdir value}"; shift 2 ;;
    --payload) payload="$(cd "${2:?missing --payload value}" && pwd)"; shift 2 ;;
    -h|--help) echo 'Usage: install.sh [--prefix /usr/local] [--destdir DIR] [--payload DIR]'; exit 0 ;;
    *) printf 'error: unknown argument %s\n' "$1" >&2; exit 2 ;;
  esac
done

case "${prefix}" in
  /*) ;;
  *)
  printf 'error: --prefix must be absolute\n' >&2; exit 2;
  ;;
esac
prefix="${prefix%/}"
case "${prefix}" in
  ''|'/'|*'/../'*|*/..)
    printf 'error: refusing unsafe --prefix %s\n' "${prefix}" >&2; exit 2
    ;;
esac
if [ -n "${destdir}" ]; then
  case "${destdir}" in
    /*) ;;
    *) printf 'error: --destdir must be absolute when provided\n' >&2; exit 2 ;;
  esac
  case "${destdir}" in
    *'/../'*|*/..) printf 'error: refusing unsafe --destdir %s\n' "${destdir}" >&2; exit 2 ;;
  esac
  destdir="${destdir%/}"
fi
test -x "${payload}/bin/morsa" || {
  printf 'error: %s is not an extracted Morsa payload\n' "${payload}" >&2; exit 3;
}
if find "${payload}" -type l -print -quit | grep -q .; then
  printf 'error: symbolic links are not accepted in installation payloads\n' >&2
  exit 3
fi

root="${destdir}${prefix}"
manifest="${root}/share/morsa/install-manifest.txt"
mkdir -p "${root}/bin" "${root}/libexec/morsa" "${root}/share/morsa" \
  "${root}/share/doc/morsa" "${root}/share/man/man1" \
  "${root}/share/bash-completion/completions" "${root}/share/zsh/site-functions" \
  "${root}/share/fish/vendor_completions.d"

install -m 0755 "${payload}/bin/morsa" "${root}/bin/morsa"
for helper in morsa-parser-host morsa-plugin-host morsa-mcp; do
  install -m 0755 "${payload}/libexec/morsa/${helper}" "${root}/libexec/morsa/${helper}"
done
install -m 0644 "${payload}/share/man/man1/morsa.1" "${root}/share/man/man1/morsa.1"
install -m 0644 "${payload}/share/bash-completion/completions/morsa" "${root}/share/bash-completion/completions/morsa"
install -m 0644 "${payload}/share/zsh/site-functions/_morsa" "${root}/share/zsh/site-functions/_morsa"
install -m 0644 "${payload}/share/fish/vendor_completions.d/morsa.fish" "${root}/share/fish/vendor_completions.d/morsa.fish"
cp -a "${payload}/share/doc/morsa/." "${root}/share/doc/morsa/"
# Normalize documentation modes even when the archive came from a permissive filesystem.
find "${root}/share/doc/morsa" -type d -exec chmod 0755 {} +
find "${root}/share/doc/morsa" -type f -exec chmod 0644 {} +
if [ -f "${payload}/uninstall.sh" ]; then
  uninstall_source="${payload}/uninstall.sh"
else
  uninstall_source="${payload}/scripts/uninstall.sh"
fi
install -m 0755 "${uninstall_source}" "${root}/share/morsa/uninstall.sh"

# The manifest makes uninstallation explicit and constrained to files we own.
cat >"${manifest}" <<EOF
${prefix}/bin/morsa
${prefix}/libexec/morsa/morsa-parser-host
${prefix}/libexec/morsa/morsa-plugin-host
${prefix}/libexec/morsa/morsa-mcp
${prefix}/share/man/man1/morsa.1
${prefix}/share/bash-completion/completions/morsa
${prefix}/share/zsh/site-functions/_morsa
${prefix}/share/fish/vendor_completions.d/morsa.fish
${prefix}/share/doc/morsa
${prefix}/share/morsa/uninstall.sh
EOF

printf 'Installed Morsa under %s\n' "${root}"
printf 'Run %s/bin/morsa doctor after opening a new shell.\n' "${prefix}"
