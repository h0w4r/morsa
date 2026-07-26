# Plugins

Morsa admite paquetes de proceso versionados y un SDK gestionado. En 1.0 los
paquetes instalados se ejecutan mediante JSON delimitado por líneas; la caída de
un plugin no derriba la CLI.

```text
example-plugin/
├── morsa-plugin.json
├── bin/example-plugin
└── LICENSE
```

```json
{
  "id": "example.reputation",
  "name": "Example Reputation",
  "version": "1.2.0",
  "author": "Example",
  "apiVersion": "1",
  "kind": "process",
  "entryPoint": "bin/example-plugin",
  "arguments": [],
  "permissions": ["network"],
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "description": "Consulta un hash explícito."
}
```

El ID cumple `^[a-z0-9][a-z0-9._-]{1,63}$`; `entryPoint` es relativo; kinds:
`process` y `dotnet-process`. Permisos reconocidos: `network`, `filesystem:read`,
`filesystem:write`, `secrets`, `process`. Se rechazan permisos desconocidos, hash
inválido, entry point ausente, ruta absoluta y traversal ZIP.

```bash
morsa plugin install ./example-plugin.zip
morsa plugin list --json
morsa plugin activate example.reputation 1.2.0
morsa plugin run example.reputation hash_lookup \
  --input '{"sha256":"..."}' --timeout 30
morsa plugin rollback example.reputation
morsa plugin remove example.reputation --version 1.2.0
```

El catálogo está en `.morsa/plugins/ID/VERSION`; `current.txt` cambia la versión
activa atómicamente. Rollback selecciona una versión anterior válida.

## Protocolo `morsa-plugin/1`

Morsa arranca sin shell, limpia el entorno heredado y expone solo `PATH`, `LANG`,
`MORSA_PLUGIN_PROTOCOL`, `MORSA_WORKSPACE` y `MORSA_PLUGIN_PERMISSIONS`.

```json
{"type":"initialize","protocol":"morsa-plugin/1","plugin_id":"example.reputation","permissions":["network"]}
{"type":"request","id":"42f...","operation":"hash_lookup","input":{"sha256":"..."}}
{"type":"result","id":"42f...","output":{"known":false}}
```

Cada valor ocupa una línea UTF-8. Se aceptan hasta 16 mensajes, una línea de hasta
4 MiB, stderr de 1 MiB y timeout explícito. Protocolo por stdout; diagnóstico por
stderr.

`Morsa.PluginSdk` ofrece `IMorsaPlugin`, `PluginManifest` e
`IMorsaPluginRegistry`; permite registrar `IArtifactExtractor` e `ISearchProvider`,
no resolver servicios arbitrarios.

## Proveedores opcionales de ejemplo

El repositorio incluye adaptadores externos completos que ejercitan la misma
frontera `morsa-plugin/1` disponible para terceros:

- `examples/Morsa.VirusTotalPlugin`: consulta de hash predeterminada; la carga de
  archivo es una operación `upload` separada y exige `explicit_upload=true`. La
  credencial se obtiene de `VT_API_KEY`, nunca del manifest ni de SQLite.
- `examples/Morsa.ShodanPlugin`: `host_lookup` acotado para una dirección IP
  explícita. La credencial se obtiene de `SHODAN_API_KEY` y se redacta en errores.

Ambos limitan las respuestas, solo aceptan overrides HTTP hacia loopback para
fixtures y traen instrucciones bilingües en sus directorios. Las pruebas usan
servidores HTTP locales; ningún gate de release necesita un secreto real.

Revisa fuente/hash, concede permisos mínimos, no empaquetes secretos y diseña
operaciones idempotentes y acotadas.
