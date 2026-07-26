# syntax=docker/dockerfile:1.12

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
ARG VERSION=v1.0.0
WORKDIR /source

# Restore project descriptors first to retain the dependency cache across edits.
COPY global.json Directory.Build.props Directory.Packages.props nuget.config Morsa.slnx ./
COPY src/Morsa.Domain/Morsa.Domain.csproj src/Morsa.Domain/
COPY src/Morsa.Application/Morsa.Application.csproj src/Morsa.Application/
COPY src/Morsa.Infrastructure/Morsa.Infrastructure.csproj src/Morsa.Infrastructure/
COPY src/Morsa.PluginSdk/Morsa.PluginSdk.csproj src/Morsa.PluginSdk/
COPY src/Morsa.Cli/Morsa.Cli.csproj src/Morsa.Cli/
COPY src/Morsa.ParserHost/Morsa.ParserHost.csproj src/Morsa.ParserHost/
COPY src/Morsa.PluginHost/Morsa.PluginHost.csproj src/Morsa.PluginHost/
COPY src/Morsa.Mcp/Morsa.Mcp.csproj src/Morsa.Mcp/
RUN version="${VERSION#v}" && export VERSION="$version" && \
    case "${TARGETARCH}" in \
      amd64) echo linux-musl-x64 >/tmp/rid ;; \
      arm64) echo linux-musl-arm64 >/tmp/rid ;; \
      *) echo "unsupported architecture: ${TARGETARCH}" >&2; exit 2 ;; \
    esac && \
    for project in \
      src/Morsa.Cli/Morsa.Cli.csproj \
      src/Morsa.ParserHost/Morsa.ParserHost.csproj \
      src/Morsa.PluginHost/Morsa.PluginHost.csproj \
      src/Morsa.Mcp/Morsa.Mcp.csproj; do \
        dotnet restore "$project" --runtime "$(cat /tmp/rid)" --disable-parallel; \
    done

COPY src ./src
COPY LICENSE NOTICE.md README.md README.es.md ./

# Publish each hostile boundary as an independent, non-trimmed executable.
RUN set -eux; \
    rid="$(cat /tmp/rid)"; version="${VERSION#v}"; export VERSION="$version"; \
    publish() { \
      project="$1"; published_binary="$2"; output="$3"; \
      dotnet publish "$project" -c Release -r "$rid" --self-contained true --no-restore \
        -p:Version="$version" \
        -p:PublishSingleFile=true -p:PublishTrimmed=false \
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false \
        -o "/tmp/$published_binary"; \
      install -D -m 0755 "/tmp/$published_binary/$published_binary" "$output"; \
    }; \
    publish src/Morsa.Cli/Morsa.Cli.csproj morsa /out/morsa; \
    publish src/Morsa.ParserHost/Morsa.ParserHost.csproj morsa-parser-host /out/libexec/morsa-parser-host; \
    publish src/Morsa.PluginHost/Morsa.PluginHost.csproj morsa-plugin-host /out/libexec/morsa-plugin-host; \
    publish src/Morsa.Mcp/Morsa.Mcp.csproj morsa-mcp /out/libexec/morsa-mcp; \
    install -D -m 0644 LICENSE /out/licenses/LICENSE; \
    install -D -m 0644 NOTICE.md /out/licenses/NOTICE.md

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
ARG VERSION=v1.0.0

LABEL org.opencontainers.image.title="Morsa" \
      org.opencontainers.image.description="Linux metadata, OSINT and authorized reconnaissance CLI" \
      org.opencontainers.image.source="https://github.com/h0w4r/morsa" \
      org.opencontainers.image.licenses="GPL-3.0-or-later" \
      org.opencontainers.image.version="${VERSION}"

RUN apk add --no-cache ca-certificates icu-libs libssl3 libstdc++ tzdata zlib && \
    addgroup -S -g 65532 morsa && \
    adduser -S -D -H -u 65532 -G morsa morsa && \
    mkdir -p /workspace /home/morsa/.config /home/morsa/.local/state /home/morsa/.cache && \
    chown -R 65532:65532 /workspace /home/morsa

COPY --from=build --chown=0:0 /out /opt/morsa

ENV MORSA_PARSER_HOST=/opt/morsa/libexec/morsa-parser-host \
    MORSA_PLUGIN_HOST=/opt/morsa/libexec/morsa-plugin-host \
    MORSA_MCP_HOST=/opt/morsa/libexec/morsa-mcp \
    XDG_CONFIG_HOME=/home/morsa/.config \
    XDG_STATE_HOME=/home/morsa/.local/state \
    XDG_CACHE_HOME=/home/morsa/.cache \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER 65532:65532
WORKDIR /workspace
VOLUME ["/workspace"]
ENTRYPOINT ["/opt/morsa/morsa"]
