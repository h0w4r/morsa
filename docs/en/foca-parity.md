# FOCA compatibility and parity matrix

## Baseline and interpretation

The comparison baseline is FOCA `v3.4.7.1` at commit
`754453ad7f9579a6021c484d5014a3cd12fd0e35`. Morsa is not a UI clone. “Equivalent”
means that an evidence-oriented Linux workflow produces the same class of useful
information with an auditable source locator. “Partial” is not counted as parity.

Status legend:

- **Native** — implemented and intended as a 1.0 equivalent.
- **Replacement** — different provider/mechanism with documented rationale.
- **Partial** — useful coverage exists, but semantic or format depth is lower.
- **Not in 1.0** — deliberately excluded or still lacks an accepted equivalent.

This matrix describes code capability. Every native legacy-format row has passed
the deterministic golden corpus and the automated differential gate against the
exact pinned FOCA source. CI rebuilds that upstream source instead of trusting a
precompiled or locally modified reference binary.

## Metadata formats

| FOCA capability/format | Morsa implementation | Status | Evidence and known difference |
|---|---|---|---|
| OOXML Word/Excel/PowerPoint | `ZipXmlMetadataExtractor` | Native | core/app/custom properties and relationships; DTD disabled and ZIP budgets |
| OpenDocument ODT/ODS/ODP | `ZipXmlMetadataExtractor` | Native | `meta.xml` creator/generator/dates/editing fields |
| PDF Info dictionary | `PdfMetadataExtractor` | Native | bounded Author/Creator/Producer/date/title/subject/keywords |
| PDF XMP | `PdfMetadataExtractor` | Native | bounded RDF attributes/lists/history plus decoded `FlateDecode` metadata streams |
| EXIF/IPTC/XMP images | `ImageMetadataExtractor` | Native | directory/tag provenance including GPS, dates, software, author |
| SVG | `SvgMetadataExtractor` | Native | comments and href/about/resource with XXE defenses |
| RDP | `TextMetadataExtractor` | Native | server, username, domain, client/host and generic configuration fields |
| ICA | `TextMetadataExtractor` | Native | server/user/domain/client and generic key/value fields |
| OLE/CFB Office | `OleMetadataExtractor` | Native | SummaryInformation, DocumentSummaryInformation/custom properties, OS, Word revision history, Ole10Native and embedded images |
| Adobe InDesign | `InDesignMetadataExtractor` | Native | FOCA-equivalent paths/printers/XMP plus bounded embedded-image metadata |
| WordPerfect | `WordPerfectMetadataExtractor` | Native | FF-WPC validation and bounded records for paths, users, printers and applications |
| Extensionless files | magic/MIME classification plus registry | Native | extractor is selected from detected content, not only extension |
| Embedded objects | dedicated PDF/OLE parsers plus ZIP static analysis | Native | covers FOCA image/object behavior without executing content; recursion is byte/count bounded |
| Revision/history recovery | OLE Word revision table plus OOXML/ODF and XMP history | Native | reproduces the upstream metadata class; it does not claim undelete of arbitrary document content |
| Corrupt artifacts | bounded extractors plus diagnostics | Native | artifact-level failure does not terminate run |

## Metadata normalization and evidence

| Capability | Status | Morsa behavior |
|---|---|---|
| users/authors | Native | normalized entity backed by `MetadataObservation` and `Evidence` |
| applications/versions | Native | application categories and correlation |
| operating systems | Native for source evidence | OLE header mapping and source tags are preserved; no unsupported speculative OS inference |
| printers | Native for FOCA formats | OLE, InDesign and WordPerfect printer evidence is parsed under bounded rules |
| paths/UNC | Native for detected values | normalized bounded path/UNC observations |
| emails, domains, hosts, URLs | Native | normalized and deduplicated with source locator |
| dates/timeline | Native for parseable values | original value retained; normalized timeline relationship |
| graph traceability | Native | each generated relation points to evidence/artifact identity |

## Search, discovery, and acquisition

| FOCA class | Morsa equivalent | Status |
|---|---|---|
| web search providers | DuckDuckGo HTML/Lite, optional SearXNG | Replacement |
| commercial search integrations | provider/plugin boundary | Replacement; API keys and real smoke depend on user secrets |
| historical URLs | Common Crawl index | Replacement |
| sitemap and robots | direct crawler provider | Native |
| download/deduplication | scoped acquisition + SHA-256 CAS | Native |
| offline/imported search results | generic text/CSV/JSON/NDJSON/HAR paths | Native where corresponding importer is selected |
| search failure isolation | persisted `ProviderRequest` and partial coverage | Native |

Morsa does not scrape providers with unstable undocumented authenticated APIs as a
hard dependency. SearXNG is explicitly bootstrap-able and self-hosted.

## Network and web reconnaissance

| Capability | Status | Notes |
|---|---|---|
| DNS A/AAAA/MX/NS/SOA/TXT/CNAME/SRV/CAA | Native | persisted `DnsObservation` |
| reverse DNS | Native CLI | bounded comma-separated address input |
| HTTP fingerprinting | Native | scoped bounded headers/technology evidence |
| TLS certificate fingerprinting | Native | scoped host/port observation |
| raw banner | Native | bounded TCP protocol hint |
| same-host crawler/map | Native | depth/page budgets and per-hop scope |
| backup candidate validation | Native | explicit aggressive mode and budget |
| AXFR | Native, bounded | explicit aggressive scope, TCP transfer, optional authoritative server |
| subdomain/range enumeration | Native, bounded | label/wordlist budget with wildcard suppression; CIDR PTR budget |
| Shodan | Plugin replacement | optional; not a built-in credential dependency |

## Reporting and platform

| Capability | Morsa | Status |
|---|---|---|
| human report | standalone script-free HTML | Native |
| machine report | schema-versioned JSON and CSV | Native |
| graphs | DOT, GraphML, GEXF, CSV | Native |
| reproducible evidence bundle | hash-addressed bundle with optional redaction | Native |
| GUI project workflow | Linux CLI + MCP | Replacement |
| Windows/SQL Server runtime | not required | Intentional difference |
| Linux x64/ARM64 glibc/musl | self-contained artifacts | Native |

## Acceptance rule

The format rows above are backed by `MetadataExtractorCorpusTests` and
`LegacyMetadataParityTests`: deterministic OOXML/ODF/PDF/SVG/RDP/ICA/image/OLE,
InDesign and WordPerfect artifacts, expected observations, malformed variants and
magic-byte checks without extensions. `tools/FocaDifferential` compiles
`MetadataExtractCore` from commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`,
runs FOCA and Morsa over the same deterministic legacy corpus, and fails whenever
Morsa omits a metadata category that FOCA emits. The JSON evidence contains the
baseline commit, per-format category counts, missing categories, and final result.

Run the same gate on Windows, where the pinned FOCA baseline targets .NET Framework:

```powershell
git clone https://github.com/ElevenPaths/FOCA .cache/upstream/foca
git -C .cache/upstream/foca checkout 754453ad7f9579a6021c484d5014a3cd12fd0e35
dotnet build tools/FocaDifferential/FocaRunner.csproj -c Release
dotnet run --project tools/FocaDifferential/Morsa.FocaDifferential.csproj -c Release -- `
  tools/FocaDifferential/bin/Release/net461/FocaRunner.exe `
  TestResults/foca-differential.json
```

The clean ports listed in `NOTICE.md` intentionally add budgets, provenance,
diagnostics and safer parsing rather than removing an upstream metadata class.
External real-document regression files can be added without changing extractor
contracts; non-redistributable documents are not silently claimed as bundled fixtures.
