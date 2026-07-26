# Proveedor VirusTotal para Morsa

Adaptador externo opcional `morsa-plugin/1` para VirusTotal API v3.

Referencias del proveedor: [reporte de archivo](https://docs.virustotal.com/reference/file-info) y [carga de archivo](https://docs.virustotal.com/reference/files-scan).

## Operaciones

- `hash_lookup`: operación pasiva predeterminada. Entrada: `{ "hash": "<md5|sha1|sha256>" }`.
- `upload`: envía un archivo local solamente si se proporcionan `path` y `explicit_upload: true`. La carga directa está limitada a 32 MiB.

Ninguna operación devuelve la clave API, la URL completa de la petición, la ruta local completa, excepciones sin filtrar ni respuestas sin límite.

## Configuración

```bash
export VT_API_KEY='reemplazar'
# Endpoint opcional para fixtures. Se exige HTTPS salvo HTTP loopback.
export VT_API_BASE_URL='http://127.0.0.1:8080/api/v3/'
```

Ambas variables aparecen en `secretEnvironmentVariables` porque el host de procesos de Morsa inicia plugins con una lista permitida de entorno.

## Compilación y smoke del protocolo

```bash
dotnet build Morsa.VirusTotalPlugin.csproj -c Release
printf '%s\n%s\n' \
  '{"type":"initialize","protocol":"morsa-plugin/1"}' \
  '{"type":"request","id":"vt-1","operation":"hash_lookup","input":{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}' \
  | dotnet run --project Morsa.VirusTotalPlugin.csproj --no-build -c Release
```

Usa un fixture loopback para las pruebas. Sin `VT_API_KEY`, la petición devuelve el error estructurado `configuration_invalid`.

## Publicación e instalación

```bash
dotnet publish Morsa.VirusTotalPlugin.csproj -c Release -r linux-x64
morsa plugin install ./directorio-publicado
```

El proyecto copia `morsa-plugin.json` junto al ejecutable durante la compilación y publicación.
