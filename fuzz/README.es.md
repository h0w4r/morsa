# Fuzzing y hardening de parsers de Morsa

Este directorio contiene un harness mutacional reproducible para las superficies de parsing de Morsa 1.0. No añade dependencias NuGet, no forma parte de la solución de producción y ejecuta cada entrada hostil en un proceso desechable.

## Superficies cubiertas

| Target | Implementación ejercitada | Corpus base | Invariantes adicionales |
|---|---|---|---|
| `magic` | `MagicByteArtifactInspector.InspectAsync` | firmas PDF, OLE, PNG, JPEG, TIFF, ZIP truncado y vacío | salida válida de tipo/MIME y terminación acotada |
| `zipxml` | `ZipXmlMetadataExtractor.ExtractAsync` | OOXML, ODF, ZIP con ruta insegura, DTD, expansión controlada y ZIP truncado | colecciones acotadas, diagnósticos con código y presupuesto de descompresión |
| `pdf` | `PdfMetadataExtractor.ExtractAsync` | PDF Info y XMP | observaciones acotadas y valores válidos |
| `svg` | `SvgMetadataExtractor.ExtractAsync` | SVG normal y DTD/entidad externa prohibida | XML sin resolución externa y terminación acotada |
| `rdp` | `TextMetadataExtractor.ExtractAsync` con `ArtifactKind.Rdp` | claves RDP normales y líneas límite | observaciones acotadas, categoría y extractor no vacíos |
| `ica` | `TextMetadataExtractor.ExtractAsync` con `ArtifactKind.Ica` | secciones ICA y separadores anómalos | observaciones acotadas, categoría y extractor no vacíos |
| `binary` | `BinaryStringsMetadataExtractor.ExtractAsync` | cadenas ASCII/XMP, cabecera OLE y bytes de control | resultados acotados y watchdog contra regex/decodificación hostil |

## Modelo de ejecución

`Morsa.FuzzHarness` tiene dos capas:

1. **Controlador:** selecciona un seed, aplica mutaciones deterministas, escribe un archivo temporal y crea un worker.
2. **Worker:** procesa una sola entrada, valida el contrato de `ExtractionResult` y termina.

El controlador mata el árbol del worker si excede `--timeout-ms + 500 ms`. Los scripts Linux añaden límites de CPU y memoria con `ulimit` y, cuando está disponible, un segundo watchdog con GNU `timeout`. Esta separación evita que una excepción fatal, un hang o una terminación nativa contamine la campaña completa.

Las mutaciones incluyen:

- cambio de bits y bytes;
- inserción y borrado de rangos;
- duplicación y truncamiento;
- sobrescritura con enteros límite;
- splice entre seeds del mismo target;
- inserción de tokens de `dictionaries/morsa.dict`;
- bytes aleatorios bajo tamaño máximo.

## Prerrequisitos Linux

- SDK o runtime .NET 10 compatible con el build del harness;
- Bash;
- `sha256sum` para verificar el corpus;
- GNU `timeout` recomendado, pero no obligatorio;
- Python 3 solo si se desea regenerar los seeds binarios/ZIP.

El harness usa el `dotnet` indicado por `DOTNET_HOST_PATH`, luego `$DOTNET_ROOT/dotnet` y finalmente el encontrado en `PATH`.

## Smoke reproducible

Desde la raíz del repositorio:

```bash
bash ./fuzz/scripts/verify-corpus.sh
bash ./fuzz/scripts/smoke.sh
```

El smoke reproduce cada seed exactamente una vez. La señal esperada es un resumen JSON con `findings: 0` y código de salida `0`:

```json
{
  "schema_version": "morsa-fuzz-summary/1",
  "target": "all",
  "seed": 1297044051,
  "seed_only": true,
  "executions": 25,
  "findings": 0,
  "total_time_budget_exhausted": false
}
```

## Campaña acotada

```bash
MORSA_FUZZ_TARGET=all \
MORSA_FUZZ_ITERATIONS=10000 \
MORSA_FUZZ_TIMEOUT_MS=2000 \
MORSA_FUZZ_TOTAL_SECONDS=900 \
MORSA_FUZZ_MAX_INPUT_BYTES=1048576 \
MORSA_FUZZ_MEMORY_MB=1536 \
MORSA_FUZZ_SEED=1297044051 \
bash ./fuzz/scripts/run-bounded.sh
```

`MORSA_FUZZ_ITERATIONS` se aplica **por target**. El presupuesto global de tiempo tiene precedencia y corta limpiamente el controlador cuando se agota.

## Campaña continua

```bash
MORSA_FUZZ_TARGET=all \
MORSA_FUZZ_CHUNK_SECONDS=900 \
bash ./fuzz/scripts/run-continuous.sh
```

La campaña continua divide la ejecución en rondas finitas y registra una semilla diferente por ronda. Se detiene al primer hallazgo porque `run-bounded.sh` activa `--stop-on-finding`; esto evita enterrar el primer crash bajo ruido posterior.

## Ejecución directa

```bash
dotnet build ./fuzz/Morsa.FuzzHarness/Morsa.FuzzHarness.csproj -c Release
dotnet ./fuzz/Morsa.FuzzHarness/bin/Release/net10.0/Morsa.FuzzHarness.dll \
  --target svg \
  --corpus ./fuzz/corpus \
  --dictionary ./fuzz/dictionaries/morsa.dict \
  --output ./fuzz/artifacts \
  --iterations 5000 \
  --timeout-ms 1500 \
  --max-input-bytes 1048576 \
  --max-total-seconds 300 \
  --seed 1297044051
```

## Presupuestos y códigos de salida

| Control | Predeterminado | Propósito |
|---|---:|---|
| `--max-input-bytes` | 1 MiB | evita asignaciones descontroladas antes del parser |
| `ExtractionOptions.MaxUncompressedBytes` | `min(16 * input, 64 MiB)` | limita expansión de contenedores |
| `ExtractionOptions.MaxContainerEntries` | 2.000 | limita fan-out ZIP |
| `ExtractionOptions.MaxDepth` | 4 | reserva de contrato para contenedores anidados |
| `--timeout-ms` | 2.000 ms | watchdog cooperativo del parser |
| watchdog externo | timeout + 500 ms | mata workers que ignoran cancelación |
| `--max-total-seconds` | 300 s | presupuesto completo de campaña |

| Código | Significado |
|---:|---|
| `0` | campaña terminada sin hallazgos |
| `1` | al menos un crash, timeout o violación de invariante |
| `2` | argumento inválido |
| `3` | worker rechazó una entrada ausente o sobredimensionada |
| `4` | fallo interno del controlador |
| `100` | excepción no controlada del parser |
| `101` | violación del contrato de salida |
| `124` | timeout cooperativo o watchdog externo |

Una salida no nula de cualquier worker se clasifica como hallazgo, incluso si proviene de una señal nativa no enumerada.

## Triage y reproducción

Los hallazgos se escriben en:

```text
fuzz/artifacts/findings/<target>/<sha256-corto>/
├── finding.json
└── input.<extensión>
```

`finding.json` contiene `schema_version`, target, iteración, semilla del controlador, seed de origen, SHA-256, tamaño, código de salida, timeout y salida acotada del worker. Para reproducir:

```bash
bash ./fuzz/scripts/reproduce.sh svg ./fuzz/artifacts/findings/svg/0123456789abcdef/input.svg
```

El directorio `fuzz/artifacts/` está ignorado localmente para impedir que muestras potencialmente hostiles se publiquen por accidente.

## Regeneración y validación del corpus

Los seeds de texto se mantienen directamente en `corpus/`. Los ZIP y binarios se generan de forma determinista:

```bash
python3 ./fuzz/scripts/generate-corpus.py
bash ./fuzz/scripts/verify-corpus.sh
```

`SHA256SUMS` fija cada seed y el diccionario. Una modificación intencional del corpus exige regenerar el manifiesto, revisar el diff y repetir el smoke.

## Criterio del gate

El gate de fuzzing se considera verde cuando:

1. el proyecto compila sin warnings;
2. `verify-corpus.sh` valida todos los hashes;
3. `smoke.sh` ejecuta todos los seeds con cero hallazgos;
4. una campaña mutacional acotada ejecuta todos los targets con cero hallazgos no explicados;
5. cada timeout/crash conserva una reproducción y hace fallar el proceso;
6. `fuzz/artifacts/` permanece fuera del control de versiones.

Un diagnóstico esperado del parser —por ejemplo, ZIP truncado, DTD prohibido o ruta ZIP insegura— no es un crash y debe regresar mediante `ExtractionDiagnostic`.
