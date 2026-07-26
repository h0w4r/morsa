# Instalación

Los binarios de Morsa son self-contained: no necesitan un runtime .NET instalado.
Linux sí aporta las bibliotecas nativas C/C++, ICU, TLS y certificados raíz.

## Elegir el artefacto

```bash
uname -m
ldd --version 2>&1 | head -1
```

| Host | Artefacto |
|---|---|
| x86-64 con glibc | `morsa-VERSION-linux-x64.tar.gz` |
| ARM64 con glibc | `morsa-VERSION-linux-arm64.tar.gz` |
| x86-64 con musl/Alpine | `morsa-VERSION-linux-musl-x64.tar.gz` |
| ARM64 con musl/Alpine | `morsa-VERSION-linux-musl-arm64.tar.gz` |

Los DEB/RPM se generan para glibc. OCI incluye `linux/amd64` y `linux/arm64`.

## Verificar antes de instalar

```bash
sha256sum --check SHA256SUMS --ignore-missing
gh attestation verify morsa-1.0.0-linux-x64.tar.gz -R h0w4r/morsa
```

No instales un artefacto ausente en el manifest, con hash incorrecto o con una
attestation de otro repositorio/ref. Revisa [verificación](verificacion-release.md).

## tar.gz en el sistema

```bash
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo sh ./morsa-1.0.0-linux-x64/install.sh --prefix /usr/local
morsa doctor
```

El archivo contiene `bin/`, `libexec/`, `share/`, `install.sh` y la documentación
EN/ES completa en `share/doc/morsa/docs/`; el instalador no descarga componentes.

## Instalación sin privilegios

```bash
sh ./morsa-1.0.0-linux-x64/install.sh \
  --prefix "$HOME/.local"
export PATH="$HOME/.local/bin:$PATH"
morsa doctor
```

## Debian, Ubuntu y Kali

```bash
sudo apt install ./morsa-1.0.0-linux-x64.deb
morsa doctor
sudo apt remove morsa
```

`apt` resuelve las dependencias nativas. La desinstalación no borra workspaces.

## Fedora y compatibles con RHEL

```bash
sudo dnf install ./morsa-1.0.0-linux-x64.rpm
morsa doctor
sudo dnf remove morsa
```

Para ARM64 usa el paquete `linux-arm64`. No se necesita EPEL.

## Arch

Instala el tar glibc en `/usr/local` o `$HOME/.local`. El smoke test oficial
ejecuta el payload x64 en una imagen Arch limpia y actualizada.

## Alpine

```bash
sudo apk add ca-certificates icu-libs libssl3 libstdc++ zlib
tar -xzf morsa-1.0.0-linux-musl-x64.tar.gz
sh ./morsa-1.0.0-linux-musl-x64/install.sh \
  --prefix "$HOME/.local"
```

Usa el RID musl nativo; no metas el binario glibc por una capa de compatibilidad
porque para eso ya existe el artefacto correcto. Suficiente teatro hace `ldd`.

## OCI

```bash
docker pull ghcr.io/h0w4r/morsa:v1.0.0
docker run --rm -v "$PWD:/workspace" ghcr.io/h0w4r/morsa:v1.0.0 version
docker run --rm -it -v "$PWD:/workspace" ghcr.io/h0w4r/morsa:v1.0.0 init .
```

El contenedor corre como UID/GID `65532`, usa `/workspace` y deshabilita el IPC
de diagnóstico .NET. El volumen debe ser escribible por esa identidad.

## Compilar desde fuente

```bash
bash scripts/install-dotnet.sh
export PATH="$PWD/.dotnet:$PATH"
dotnet restore Morsa.slnx --disable-parallel
dotnet build Morsa.slnx -c Release --no-restore
dotnet test Morsa.slnx -c Release --no-build
```

El instalador local valida exactamente el SDK `10.0.302` y no altera .NET global.
Si el repositorio está compartido con Windows, el host Linux se instala en
`.dotnet-linux`; usa la ruta que imprime el script.

## Desinstalar un tar

```bash
sudo sh /usr/local/share/morsa/uninstall.sh --prefix /usr/local
# o
sh "$HOME/.local/share/morsa/uninstall.sh" --prefix "$HOME/.local"
```

Solo se eliminan rutas de `share/morsa/install-manifest.txt`. Workspaces, artefactos,
reportes, estado XDG y configuración de proxies permanecen intactos.
