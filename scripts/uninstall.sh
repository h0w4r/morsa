#!/bin/sh
set -eu

# Removes only the paths recorded by Morsa's own installation manifest.
prefix='/usr/local'
destdir=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --prefix) prefix="${2:?missing --prefix value}"; shift 2 ;;
    --destdir) destdir="${2:?missing --destdir value}"; shift 2 ;;
    -h|--help) echo 'Usage: uninstall.sh [--prefix /usr/local] [--destdir DIR]'; exit 0 ;;
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
root="${destdir}${prefix}"
manifest="${root}/share/morsa/install-manifest.txt"
test -f "${manifest}" || {
  printf 'error: installation manifest not found at %s\n' "${manifest}" >&2; exit 3;
}

while IFS= read -r installed; do
  case "${installed}" in
    "${prefix}/"*) ;;
    *) printf 'error: unsafe manifest entry %s\n' "${installed}" >&2; exit 4 ;;
  esac
  target="${destdir}${installed}"
  # Prefix and traversal checks above constrain removals without GNU realpath.
  case "${target}" in
    *'/../'*|*/..) printf 'error: unsafe target %s\n' "${target}" >&2; exit 4 ;;
  esac
  if [ -d "${target}" ] && [ ! -L "${target}" ]; then
    rm -rf -- "${target}"
  else
    rm -f -- "${target}"
  fi
done <"${manifest}"
rm -f -- "${manifest}"
rmdir "${root}/share/morsa" 2>/dev/null || true
rmdir "${root}/libexec/morsa" 2>/dev/null || true

printf 'Uninstalled Morsa from %s\n' "${root}"
