# Morsa

Morsa is a Linux-first metadata, OSINT, reconnaissance and surface-analysis CLI inspired by FOCA.

> Current status: early `0.1.0-alpha.1` foundation. The public API is not stable yet.

## Build

```bash
dotnet restore Morsa.slnx
dotnet build Morsa.slnx -c Release
dotnet test Morsa.slnx -c Release
```

## Implemented foundation

- self-contained workspaces backed by SQLite;
- content-addressable artifact ingestion;
- safe initial OOXML/ODF, PDF, image, SVG, text and binary metadata extractors;
- evidence-ready normalized observations;
- proxy pools with sticky, round-robin, random, weighted, least-latency and failover selection;
- HTTP CONNECT, SOCKS4, SOCKS5 and SOCKS5h transports;
- proxy health, cooldowns, leases and network attempt journaling;
- JSON schema envelope version 1.

See `morsa --help` for available commands.

## License and provenance

Morsa is licensed under GPL-3.0-or-later. Its compatibility baseline is FOCA v3.4.7.1 at commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`.

