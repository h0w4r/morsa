# Proveedor Shodan para Morsa

Adaptador externo opcional `morsa-plugin/1` para la API REST de Shodan.

Referencia del proveedor: [API Host Information de Shodan](https://developer.shodan.io/api#shodan-host).

## Operación

`host_lookup` acepta una IP literal y los flags oficiales opcionales `history` y `minify`:

```json
{"ip":"203.0.113.10","history":false,"minify":false}
```

La respuesta contiene campos acotados del host y hasta 512 banners normalizados. Nunca devuelve la clave API, la URL completa de petición, excepciones sin filtrar ni respuestas sin límite.

## Configuración

```bash
export SHODAN_API_KEY='reemplazar'
# Endpoint opcional para fixtures. Se exige HTTPS salvo HTTP loopback.
export SHODAN_API_BASE_URL='http://127.0.0.1:8080/'
```

Ambas variables aparecen en `secretEnvironmentVariables` porque el host de procesos de Morsa inicia plugins con una lista permitida de entorno.

## Compilación y smoke del protocolo

```bash
dotnet build Morsa.ShodanPlugin.csproj -c Release
printf '%s\n%s\n' \
  '{"type":"initialize","protocol":"morsa-plugin/1"}' \
  '{"type":"request","id":"shodan-1","operation":"host_lookup","input":{"ip":"203.0.113.10","minify":true}}' \
  | dotnet run --project Morsa.ShodanPlugin.csproj --no-build -c Release
```

Usa un fixture loopback para las pruebas. Sin `SHODAN_API_KEY`, la petición devuelve el error estructurado `configuration_invalid`.

## Publicación e instalación

```bash
dotnet publish Morsa.ShodanPlugin.csproj -c Release -r linux-x64
morsa plugin install ./directorio-publicado
```

El proyecto copia `morsa-plugin.json` junto al ejecutable durante la compilación y publicación.
