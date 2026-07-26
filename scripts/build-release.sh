#!/usr/bin/env bash
set -Eeuo pipefail

# Creates one deterministic, self-contained Linux payload and its tar archive.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
version=""
rid=""
publish_only="false"

usage() {
  cat <<'EOF'
Usage: scripts/build-release.sh --version VERSION --rid RID [--publish-only]

Supported RIDs: linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64.
EOF
}

while (($#)); do
  case "$1" in
    --version) version="${2:?missing value for --version}"; shift 2 ;;
    --rid) rid="${2:?missing value for --rid}"; shift 2 ;;
    --publish-only) publish_only="true"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'error: unknown argument %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || {
  printf 'error: --version must be a semantic version\n' >&2
  exit 2
}

case "${rid}" in
  linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64) ;;
  *) printf 'error: unsupported RID %s\n' "${rid}" >&2; exit 2 ;;
esac

if [[ -n "${DOTNET:-}" ]]; then
  dotnet_cmd="${DOTNET}"
elif [[ -x "${ROOT}/.dotnet/dotnet" ]]; then
  dotnet_cmd="${ROOT}/.dotnet/dotnet"
else
  dotnet_cmd="$(command -v dotnet || true)"
fi
[[ -n "${dotnet_cmd}" ]] || {
  printf 'error: dotnet was not found; run scripts/install-dotnet.sh first\n' >&2
  exit 3
}

readonly STAGE="${ROOT}/artifacts/stage/${rid}"
readonly WORK="${ROOT}/artifacts/publish/${rid}"
readonly DIST="${ROOT}/artifacts/dist"
rm -rf "${STAGE}" "${WORK}"
mkdir -p "${STAGE}/bin" "${STAGE}/libexec/morsa" \
  "${STAGE}/share/doc/morsa" "${STAGE}/share/man/man1" \
  "${STAGE}/share/bash-completion/completions" \
  "${STAGE}/share/zsh/site-functions" \
  "${STAGE}/share/fish/vendor_completions.d" "${WORK}" "${DIST}"

publish_component() {
  local project="$1"
  local published_binary="$2"
  local installed_binary="$3"
  local destination="$4"
  local output="${WORK}/${installed_binary}"

  "${dotnet_cmd}" restore "${ROOT}/${project}" --runtime "${rid}" --disable-parallel
  "${dotnet_cmd}" publish "${ROOT}/${project}" \
    --configuration Release \
    --runtime "${rid}" \
    --self-contained true \
    --no-restore \
    -p:Version="${version}" \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    --output "${output}"

  test -f "${output}/${published_binary}"
  install -m 0755 "${output}/${published_binary}" "${destination}"
}

publish_component 'src/Morsa.Cli/Morsa.Cli.csproj' 'morsa' 'morsa' "${STAGE}/bin/morsa"
publish_component 'src/Morsa.ParserHost/Morsa.ParserHost.csproj' 'morsa-parser-host' 'morsa-parser-host' "${STAGE}/libexec/morsa/morsa-parser-host"
publish_component 'src/Morsa.PluginHost/Morsa.PluginHost.csproj' 'morsa-plugin-host' 'morsa-plugin-host' "${STAGE}/libexec/morsa/morsa-plugin-host"
publish_component 'src/Morsa.Mcp/Morsa.Mcp.csproj' 'morsa-mcp' 'morsa-mcp' "${STAGE}/libexec/morsa/morsa-mcp"

install -m 0644 "${ROOT}/LICENSE" "${STAGE}/share/doc/morsa/LICENSE"
install -m 0644 "${ROOT}/NOTICE.md" "${STAGE}/share/doc/morsa/NOTICE.md"
install -m 0644 "${ROOT}/README.md" "${STAGE}/share/doc/morsa/README.md"
install -m 0644 "${ROOT}/README.es.md" "${STAGE}/share/doc/morsa/README.es.md"
# Ship the complete bilingual documentation for offline installations.
cp -a "${ROOT}/docs" "${STAGE}/share/doc/morsa/docs"
find "${STAGE}/share/doc/morsa/docs" -type d -exec chmod 0755 {} +
find "${STAGE}/share/doc/morsa/docs" -type f -exec chmod 0644 {} +
install -m 0644 "${ROOT}/man/morsa.1" "${STAGE}/share/man/man1/morsa.1"
install -m 0644 "${ROOT}/completions/morsa.bash" "${STAGE}/share/bash-completion/completions/morsa"
install -m 0644 "${ROOT}/completions/_morsa" "${STAGE}/share/zsh/site-functions/_morsa"
install -m 0644 "${ROOT}/completions/morsa.fish" "${STAGE}/share/fish/vendor_completions.d/morsa.fish"
install -m 0755 "${ROOT}/scripts/install.sh" "${STAGE}/install.sh"
install -m 0755 "${ROOT}/scripts/uninstall.sh" "${STAGE}/uninstall.sh"

cat >"${STAGE}/share/doc/morsa/BUILD-INFO" <<EOF
name=Morsa
version=${version}
rid=${rid}
commit=$(git -C "${ROOT}" rev-parse HEAD 2>/dev/null || printf 'unknown')
source_date_epoch=$(git -C "${ROOT}" log -1 --format=%ct 2>/dev/null || date +%s)
framework=net10.0
self_contained=true
trimmed=false
EOF

if [[ "${publish_only}" == "true" ]]; then
  printf 'Published %s payload at %s\n' "${rid}" "${STAGE}"
  exit 0
fi

archive="${DIST}/morsa-${version}-${rid}.tar.gz"
source_date_epoch="${SOURCE_DATE_EPOCH:-$(git -C "${ROOT}" log -1 --format=%ct 2>/dev/null || date +%s)}"

# GNU tar produces stable ownership, ordering and timestamps across CI reruns.
tar --sort=name \
  --mtime="@${source_date_epoch}" \
  --owner=0 --group=0 --numeric-owner \
  --transform="s,^,morsa-${version}-${rid}/," \
  -C "${STAGE}" -cf - bin install.sh libexec share uninstall.sh | gzip -n >"${archive}"

printf 'Created %s\n' "${archive}"
