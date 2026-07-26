# Morsa

Morsa es una CLI diseñada para cualquier Linux que extrae metadatos, descubre
recursos públicos, correlaciona evidencias y ejecuta reconocimiento dentro de un
alcance explícito. Está inspirada en FOCA y construida en .NET 10 como monolito
modular, con procesos separados para parsers y plugins.

[Documentación en inglés](README.md) · [Índice documental](docs/README.md) ·
[Política de seguridad](SECURITY.md) · [Referencia CLI](docs/es/referencia-cli.md)

## Qué entrega la línea 1.0

- Ingesta content-addressable por SHA-256 y ejecuciones/tareas durables en SQLite.
- Extracción acotada de OOXML, ODF, PDF, imágenes, SVG, RDP, ICA, OLE/CFB,
  InDesign, WordPerfect, archivos sin extensión y formatos desconocidos.
- Discovery mediante DuckDuckGo HTML/Lite, SearXNG, Common Crawl, sitemap,
  robots, crawler directo e importadores genéricos.
- Adquisición con validación de alcance, redirecciones, SSRF, bytes, concurrencia
  y presupuestos de solicitudes.
- Pools HTTP, HTTPS CONNECT, SOCKS4, SOCKS5 y SOCKS5h con rotación automática,
  cooldown, leases, aislamiento sticky y fallback acotado.
- Entidades y relaciones trazables, timeline, JSON/CSV/HTML, GraphML/GEXF/DOT y
  bundles reproducibles de evidencia.
- DNS, TLS, HTTP y banners acotados; web mapping; ClamAV y YARA opcionales.
- Plugins de proceso `morsa-plugin/1` y servidor MCP exclusivamente por `stdio`.
- Binarios self-contained para x64/ARM64 en glibc/musl, DEB, RPM y OCI multiarch.

La [matriz de paridad con FOCA](docs/es/paridad-foca.md) distingue capacidades
nativas, equivalencias justificadas y diferencias conocidas. Un gate Windows de CI
compila el commit fijado de FOCA y compara ambos extractores sobre el mismo corpus.
Sin humo; bastante de eso produce ya cualquier proxy gratuito.

## Plataformas Linux

| RID | ABI | Arquitectura | Entrega |
|---|---|---|---|
| `linux-x64` | glibc | x86-64 | tar.gz, DEB, RPM |
| `linux-arm64` | glibc | ARM64 | tar.gz, DEB, RPM |
| `linux-musl-x64` | musl | x86-64 | tar.gz, OCI |
| `linux-musl-arm64` | musl | ARM64 | tar.gz, OCI |

Los smoke tests usan contenedores limpios de Debian, Ubuntu, Kali, Fedora, Arch y
Alpine. WSL sirve como laboratorio de desarrollo, pero Morsa no depende de WSL.

## Instalar una release

Descarga el archivo correspondiente y `SHA256SUMS` desde GitHub Releases:

```bash
sha256sum --check SHA256SUMS --ignore-missing
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo bash ./morsa-1.0.0-linux-x64/install.sh
morsa doctor
```

Consulta [instalación](docs/es/instalacion.md) y
[verificación de releases](docs/es/verificacion-release.md) para DEB, RPM, OCI,
prefijos sin privilegios y desinstalación.

## Flujo rápido

```bash
mkdir investigacion && cd investigacion
morsa init . --name ejemplo
morsa scope add example.org --kind domain --max-mode active

morsa ingest file ./documento.pdf
morsa analyze all
morsa correlate
morsa report html --output ./reports/reporte.html

morsa run full example.org --providers duckduckgo,commoncrawl
morsa run resume
```

Todas las respuestas de máquina llevan `schema_version`. Los logs estructurados
van por stderr; MCP conserva stdout exclusivamente para su protocolo.

## Pools de proxies con rotación automática

```bash
morsa proxy pool add investigacion \
  --policy least-latency \
  --max-rotations 5 \
  --max-attempts 8
morsa proxy import ./proxies.ndjson --pool investigacion
morsa proxy test investigacion --url https://example.org/
morsa run full example.org --proxy-pool investigacion
```

La rotación es finita y responde a fallos DNS/connect/TLS, autenticación del
proxy, `403`, `429`, `5xx` configurados o challenge detectado. Cuando el perfil
exige proxy no existe fallback directo silencioso. Detalles en la
[guía de proxies](docs/es/proxies.md).

## Compilar

El repositorio fija el SDK `10.0.302`:

```bash
bash scripts/install-dotnet.sh
export PATH="$PWD/.dotnet:$PATH"
dotnet restore Morsa.slnx --disable-parallel
dotnet build Morsa.slnx -c Release --no-restore
dotnet test Morsa.slnx -c Release --no-build
```

Para generar un payload reproducible:

```bash
bash scripts/build-release.sh --version 1.0.0 --rid linux-x64
```

## Verificación de cadena de suministro

Cada release incluye SHA-256, SBOM SPDX/CycloneDX y attestations firmadas con una
identidad efímera Sigstore/OIDC de GitHub Actions:

```bash
sha256sum --check SHA256SUMS
gh attestation verify morsa-1.0.0-linux-x64.tar.gz -R h0w4r/morsa
```

## Documentación

- [Arquitectura](docs/es/arquitectura.md)
- [Referencia CLI](docs/es/referencia-cli.md)
- [Alcance y niveles de actividad](docs/es/alcance.md)
- [Pools de proxies](docs/es/proxies.md)
- [Plugins y protocolo JSONL](docs/es/plugins.md)
- [Servidor MCP](docs/es/mcp.md)
- [Modelo de amenazas](docs/es/modelo-amenazas.md)
- [Paridad FOCA](docs/es/paridad-foca.md)
- [Actualización](docs/es/actualizacion.md)
- [Diagnóstico](docs/es/diagnostico.md)

## Licencia y procedencia

Morsa usa `GPL-3.0-or-later`. El baseline de compatibilidad es FOCA `v3.4.7.1`,
commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`. Todo port selectivo debe
conservar atribución, ruta upstream, commit y hash según `NOTICE.md`.
