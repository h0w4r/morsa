# Notices

Morsa uses FOCA v3.4.7.1 (`ElevenPaths/FOCA`, commit `754453ad7f9579a6021c484d5014a3cd12fd0e35`) as its functional compatibility baseline.

The following files are selective, safety-oriented clean ports of useful FOCA behavior and carry explicit SPDX and upstream references:

- `src/Morsa.Infrastructure/Metadata/LegacyDocumentMetadataExtractors.cs` from `InDDDocument.cs` and `WPDDocument.cs`.
- `src/Morsa.Infrastructure/Metadata/OleMetadataExtractor.cs` from `Office972003.cs` and `OleDocument.cs`.
- `src/Morsa.Infrastructure/Metadata/PdfMetadataExtractor.cs` from `PDFDocument.cs` and `XMPExtractor.cs`.
- `src/Morsa.Infrastructure/Metadata/StructuredMetadataUtilities.cs` from `XMPExtractor.cs`.

The exact upstream SHA-256 inventory is published in `upstream/FOCA-v3.4.7.1.sha256`; the relevant source hashes are:

- `InDDDocument.cs`: `74db82537b8ca2a4268a2b078a8a2e07823688a84efa7b5dfd5dc8f72a283812`
- `WPDDocument.cs`: `3b3e93a3651fa94a29d48c1cb38cbd07e56d2863e5a7ca5ecf9481e993793e8c`
- `Office972003.cs`: `6b2c06e2f88de386f533c5738cdd6e59d0ff21be03f89fad747c9de9bcedb6fa`
- `OleDocument.cs`: `48fd68ef1c2cc954c35e0c23774c57939461eebef8f7a48bc77f246d19f15f1f`
- `PDFDocument.cs`: `309642dd43b9ac580aa5a604f2ac992a265545af43f3bc989f4fada57d2135b2`
- `XMPExtractor.cs`: `efacf744f388de2d6444b206a11cec8b24b553df2102089ad15e5877f36c2dc0`
