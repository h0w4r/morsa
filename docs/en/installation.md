# Installation

Morsa release binaries are self-contained: a system-wide .NET runtime is not
required. Linux still supplies native C/C++ runtime, ICU, TLS, and CA libraries.

## Select the correct artifact

```bash
uname -m
ldd --version 2>&1 | head -1
```

| Host | Artifact |
|---|---|
| x86-64, glibc | `morsa-VERSION-linux-x64.tar.gz` |
| ARM64/aarch64, glibc | `morsa-VERSION-linux-arm64.tar.gz` |
| x86-64, Alpine/musl | `morsa-VERSION-linux-musl-x64.tar.gz` |
| ARM64/aarch64, Alpine/musl | `morsa-VERSION-linux-musl-arm64.tar.gz` |

DEB and RPM packages are emitted for the two glibc RIDs. OCI images contain
musl builds for `linux/amd64` and `linux/arm64`.

## Verify before install

Download the asset, `SHA256SUMS`, and optionally its SPDX/CycloneDX SBOMs:

```bash
sha256sum --check SHA256SUMS --ignore-missing
gh attestation verify morsa-1.0.0-linux-x64.tar.gz -R h0w4r/morsa
```

Do not install an asset when the checksum file does not contain it, the digest
fails, or the attestation points at another repository/ref. See
[release verification](release-verification.md) for the full procedure.

## tar.gz: system installation

The archive is relocatable and contains `bin/`, `libexec/`, and `share/`,
including the complete English/Spanish documentation under
`share/doc/morsa/docs/`:

```bash
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo sh ./morsa-1.0.0-linux-x64/install.sh --prefix /usr/local
morsa doctor
```

The archive carries its own offline installer. It never downloads components.

## tar.gz: unprivileged installation

```bash
mkdir -p "$HOME/.local"
sh ./morsa-1.0.0-linux-x64/install.sh \
  --prefix "$HOME/.local"
export PATH="$HOME/.local/bin:$PATH"
morsa doctor
```

Add `$HOME/.local/bin` to the shell profile. Completions are installed below the
selected prefix and may need to be added to the shell's completion path.

## Debian, Ubuntu, and Kali

```bash
sudo apt install ./morsa-1.0.0-linux-x64.deb
morsa doctor
sudo apt remove morsa
```

`apt` resolves native dependencies. Workspace files in user directories are not
removed by package uninstall.

## Fedora and RHEL-compatible distributions

```bash
sudo dnf install ./morsa-1.0.0-linux-x64.rpm
morsa doctor
sudo dnf remove morsa
```

For ARM64, use the `linux-arm64` package. Morsa does not require EPEL.

## Arch Linux

Use the glibc tar archive and `/usr/local`, or install it under `$HOME/.local`.
The clean-container workflow validates that the x64 payload starts with current
Arch runtime libraries.

## Alpine

```bash
sudo apk add ca-certificates icu-libs libssl3 libstdc++ zlib
tar -xzf morsa-1.0.0-linux-musl-x64.tar.gz
sh ./morsa-1.0.0-linux-musl-x64/install.sh \
  --prefix "$HOME/.local"
```

Never run a glibc artifact through a compatibility shim when a native musl RID is
available; use the musl build.

## OCI

```bash
docker pull ghcr.io/h0w4r/morsa:v1.0.0
docker run --rm -v "$PWD:/workspace" ghcr.io/h0w4r/morsa:v1.0.0 version
docker run --rm -it -v "$PWD:/workspace" ghcr.io/h0w4r/morsa:v1.0.0 init .
```

The container runs as UID/GID `65532`, uses `/workspace`, and disables .NET
diagnostic IPC. Ensure the mounted workspace is writable by that identity.

## Build from source

```bash
bash scripts/install-dotnet.sh
export PATH="$PWD/.dotnet:$PATH"
dotnet restore Morsa.slnx --disable-parallel
dotnet build Morsa.slnx -c Release --no-restore
dotnet test Morsa.slnx -c Release --no-build
```

The local SDK installer validates the exact `10.0.302` version and does not modify
the machine-wide .NET installation. In a repository shared with Windows, the Linux
host is installed in `.dotnet-linux`; use the path printed by the script.

## Uninstall a tar installation

```bash
sudo sh /usr/local/share/morsa/uninstall.sh --prefix /usr/local
# or
sh "$HOME/.local/share/morsa/uninstall.sh" --prefix "$HOME/.local"
```

Only paths recorded by `share/morsa/install-manifest.txt` are removed. Workspaces,
artifacts, reports, XDG state, and user proxy configuration remain untouched.
