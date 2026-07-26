# Compatibilidad y paridad con FOCA

Baseline: FOCA `v3.4.7.1`, commit
`754453ad7f9579a6021c484d5014a3cd12fd0e35`. Morsa no clona la GUI. “Equivalente”
significa obtener la misma clase de información útil en Linux con trazabilidad.
**Parcial no cuenta como paridad completa.**

Estados: **Nativo**, **Reemplazo**, **Parcial**, **Fuera de 1.0**. Cada fila nativa
de formatos heredados superó el corpus dorado y el gate diferencial automatizado
contra el código exacto de FOCA fijado. CI recompila ese upstream; no confía en un
binario de referencia precompilado o alterado localmente.

## Formatos

| Capacidad | Implementación | Estado | Diferencia conocida |
|---|---|---|---|
| OOXML Word/Excel/PowerPoint | `ZipXmlMetadataExtractor` | Nativo | core/app/custom y relationships con límites ZIP/XML |
| ODF ODT/ODS/ODP | `ZipXmlMetadataExtractor` | Nativo | `meta.xml`, autor/generador/fechas/edición |
| PDF Info | `PdfMetadataExtractor` | Nativo | campos comunes acotados |
| PDF XMP | `PdfMetadataExtractor` | Nativo | atributos/listas/historial RDF acotados y streams `FlateDecode` decodificados |
| EXIF/IPTC/XMP imagen | `ImageMetadataExtractor` | Nativo | tags con procedencia, GPS/fechas/software/autor |
| SVG | `SvgMetadataExtractor` | Nativo | comentarios y links, XXE bloqueado |
| RDP/ICA | `TextMetadataExtractor` | Nativo | server/user/domain/client/config |
| OLE/CFB | `OleMetadataExtractor` | Nativo | SummaryInformation, DocumentSummary/custom, OS, historial Word, Ole10Native e imágenes embebidas |
| InDesign | `InDesignMetadataExtractor` | Nativo | rutas/impresoras/XMP equivalentes a FOCA e imágenes embebidas acotadas |
| WordPerfect | `WordPerfectMetadataExtractor` | Nativo | firma FF-WPC y registros acotados de rutas, usuarios, impresoras y aplicaciones |
| Sin extensión | magic/MIME + registry | Nativo | selección por contenido |
| Objetos embebidos | parsers PDF/OLE y análisis ZIP | Nativo | cubre la clase de evidencia de FOCA sin ejecutar contenido y con presupuestos |
| Historial/revisión | tabla Word OLE, OOXML/ODF y XMP | Nativo | paridad de metadatos; no promete undelete arbitrario |
| Archivo corrupto | diagnóstico por artefacto | Nativo | no derriba el run |

Usuarios, aplicaciones, emails, dominios, hosts, URLs, fechas, OS e impresoras tienen
entidades y evidencia cuando el formato las expone. Los parsers OLE, InDesign y
WordPerfect ya no dependen del fallback binario. Cada relación generada apunta al
artefacto/evidencia original.

## Discovery y red

DuckDuckGo/SearXNG reemplazan búsqueda web; Common Crawl cubre historia; sitemap,
robots, adquisición y SHA-256 CAS son nativos. El fallo de provider se persiste sin
borrar cobertura ajena. APIs comerciales se agregan como plugins y sus smoke reales
dependen de secretos del usuario.

DNS A/AAAA/MX/NS/SOA/TXT/CNAME/SRV/CAA, reverse, subdominios con supresión de
wildcard, PTR por CIDR, AXFR autorizado, HTTP, TLS, banner, crawler y backups
presupuestados están implementados. Shodan es plugin opcional.

## Reporting/plataforma

HTML sin scripts, JSON/CSV versionado, DOT/GraphML/GEXF, bundle reproducible y
Linux self-contained x64/ARM64 glibc/musl son nativos. CLI+MCP reemplazan GUI;
Windows, WSL y SQL Server no son requisitos.

Los estados se respaldan con `MetadataExtractorCorpusTests` y
`LegacyMetadataParityTests`: artefactos deterministas OOXML/ODF/PDF/SVG/RDP/ICA,
imagen/OLE/InDesign/WordPerfect, observaciones esperadas, variantes malformadas y
magic bytes sin extensión. `tools/FocaDifferential` compila `MetadataExtractCore`
desde el commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`, ejecuta FOCA y Morsa
sobre el mismo corpus heredado y falla si Morsa omite una categoría emitida por FOCA.
La evidencia JSON conserva commit, conteos por formato, categorías ausentes y resultado.

El mismo gate puede ejecutarse en Windows, donde el baseline fijado usa .NET Framework:

```powershell
git clone https://github.com/ElevenPaths/FOCA .cache/upstream/foca
git -C .cache/upstream/foca checkout 754453ad7f9579a6021c484d5014a3cd12fd0e35
dotnet build tools/FocaDifferential/FocaRunner.csproj -c Release
dotnet run --project tools/FocaDifferential/Morsa.FocaDifferential.csproj -c Release -- `
  tools/FocaDifferential/bin/Release/net461/FocaRunner.exe `
  TestResults/foca-differential.json
```

Las diferencias de los ports limpios indicados en `NOTICE.md` agregan presupuestos,
procedencia y diagnósticos sin eliminar clases de metadatos upstream. Los documentos
reales externos no redistribuibles no se presentan como fixtures incluidos.
