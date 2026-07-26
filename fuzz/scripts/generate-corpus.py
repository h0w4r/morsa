#!/usr/bin/env python3
"""Genera los seeds binarios y ZIP de forma byte-reproducible."""

from __future__ import annotations

import pathlib
import zipfile
from typing import Union


ROOT = pathlib.Path(__file__).resolve().parents[1] / "corpus"


def write_bytes(relative: str, content: bytes) -> None:
    """Escribe un seed binario creando antes su directorio."""
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)


def write_zip(relative: str, entries: dict[str, Union[str, bytes]]) -> None:
    """Crea un ZIP determinista con timestamp, permisos y orden estables."""
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(entries.items()):
            metadata = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            metadata.compress_type = zipfile.ZIP_DEFLATED
            metadata.create_system = 3
            metadata.external_attr = 0o100644 << 16
            payload = content.encode("utf-8") if isinstance(content, str) else content
            archive.writestr(metadata, payload)


def main() -> None:
    """Regenera exclusivamente los formatos que no son cómodos de versionar como texto."""
    write_bytes("magic/empty.bin", b"")
    write_bytes("magic/pdf-header.bin", b"%PDF-1.7\n")
    write_bytes("magic/ole-header.bin", bytes.fromhex("d0cf11e0a1b11ae100000000"))
    write_bytes("magic/png-header.bin", bytes.fromhex("89504e470d0a1a0a00000000"))
    write_bytes("magic/jpeg-header.bin", bytes.fromhex("ffd8ffe000000000"))
    write_bytes("magic/tiff-le-header.bin", bytes.fromhex("49492a0000000000"))
    write_bytes("magic/truncated-zip.bin", bytes.fromhex("504b0304ffff"))
    write_bytes("binary/ole-ish.bin", bytes.fromhex("d0cf11e0a1b11ae100000000fffe"))
    write_bytes("binary/mixed-bytes.bin", bytes([0, 1, 2, 3, 9, 10, 13, 31, 32, 65, 127, 128, 254, 255]))
    write_bytes("zipxml/truncated.zip", bytes.fromhex("504b030400000000ff"))

    write_zip(
        "zipxml/ooxml-minimal.zip",
        {
            "[Content_Types].xml": '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types" />',
            "docProps/core.xml": '<?xml version="1.0"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Seed Author</dc:creator><dc:title>Fuzz Seed</dc:title></cp:coreProperties>',
            "docProps/app.xml": '<?xml version="1.0"?><Properties><Application>Morsa Seed</Application><Company>Example</Company></Properties>',
            "_rels/.rels": '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Target="https://example.test/" /></Relationships>',
        },
    )
    write_zip(
        "zipxml/odf-minimal.zip",
        {
            "mimetype": "application/vnd.oasis.opendocument.text",
            "META-INF/manifest.xml": '<?xml version="1.0"?><manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" />',
            "meta.xml": '<?xml version="1.0"?><office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/"><office:meta><dc:creator>ODF Seed</dc:creator><dc:title>Fuzz Seed</dc:title></office:meta></office:document-meta>',
        },
    )
    write_zip(
        "zipxml/path-traversal.zip",
        {
            "../docProps/core.xml": '<?xml version="1.0"?><creator>Unsafe</creator>',
            "docProps/core.xml": '<?xml version="1.0"?><creator>Safe</creator>',
        },
    )
    write_zip(
        "zipxml/dtd-prohibited.zip",
        {
            "docProps/core.xml": '<?xml version="1.0"?><!DOCTYPE properties [<!ENTITY external SYSTEM "file:///etc/passwd">]><properties><creator>&external;</creator></properties>',
        },
    )
    write_zip(
        "zipxml/expansion-budget.zip",
        {
            # Dos MiB altamente comprimibles ejercitan el presupuesto sin crear un artefacto peligroso.
            "docProps/core.xml": b"A" * (2 * 1024 * 1024),
        },
    )


if __name__ == "__main__":
    main()
