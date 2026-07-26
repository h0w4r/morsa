using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenMcdf;

namespace Morsa.UnitTests;

/// <summary>Creates deterministic legacy, CFB and PDF artifacts for parity and adversarial tests.</summary>
internal sealed class LegacySyntheticCorpus : IDisposable
{
    private static readonly byte[] SummaryInformationFormatId =
        [0xe0, 0x85, 0x9f, 0xf2, 0xf9, 0x4f, 0x68, 0x10, 0xab, 0x91, 0x08, 0x00, 0x2b, 0x27, 0xb3, 0xd9];
    private static readonly byte[] DocumentSummaryInformationFormatId =
        [0x02, 0xd5, 0xcd, 0xd5, 0x9c, 0x2e, 0x1b, 0x10, 0x93, 0x97, 0x08, 0x00, 0x2b, 0x2c, 0xf9, 0xae];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-legacy-corpus", Guid.NewGuid().ToString("N"));

    public LegacySyntheticCorpus() => Directory.CreateDirectory(_root);

    public string CreateInDesign()
    {
        const string xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                       xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:xmp="http://ns.adobe.com/xap/1.0/">
                <rdf:Description xmp:CreatorTool="Adobe InDesign 20">
                  <dc:creator><rdf:Seq><rdf:li>InDesign Author</rdf:li></rdf:Seq></dc:creator>
                  <dc:title><rdf:Alt><rdf:li>Morsa Layout</rdf:li></rdf:Alt></dc:title>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        var bytes = new List<byte>();
        bytes.AddRange([0x06, 0x06, 0xed, 0xf5, 0xd8, 0x1d, 0x46, 0xe5, 0xbd, 0x31, 0xef, 0xe7, 0xfe, 0x74, 0xb7, 0x1d]);
        bytes.AddRange(Encoding.UTF8.GetBytes("@C:\\Users\\layout.user\\Documents\\catalog.indd\0winspool\0Office LaserJet\0"));
        bytes.AddRange(Encoding.UTF8.GetBytes(xmp));
        bytes.AddRange(CreateExifJpeg("Embedded InDesign Artist"));
        return Write("sample.indd", bytes.ToArray());
    }

    public string CreateWordPerfect()
    {
        var bytes = new List<byte>([0xff, 0x57, 0x50, 0x43, 0x00, 0x00]);
        AddRecord("C:\\Users\\wp.user\\Documents\\contract.wpd");
        AddRecord("Corporate LaserJet Printer");
        AddRecord("Adobe PDF Converter");
        return Write("sample.wpd", bytes.ToArray());

        void AddRecord(string value)
        {
            bytes.AddRange([0x00, 0x98]);
            bytes.AddRange(Encoding.Unicode.GetBytes(value + "\0"));
            bytes.AddRange([0x33, 0x44, 0x55]);
        }
    }

    public string CreateOleCompound()
    {
        var path = Path.Combine(_root, "sample.doc");
        using var root = RootStorage.Create(path);
        WriteStream(root, "\u0005SummaryInformation", CreatePropertySet(
            SummaryInformationFormatId,
            (1u, Int16Property(1252)),
            (2u, StringProperty("Morsa legacy report")),
            (4u, StringProperty("OLE Author")),
            (7u, StringProperty("C:\\Users\\ole.user\\Templates\\normal.dot")),
            (8u, StringProperty("OLE Last Editor")),
            (12u, FileTimeProperty(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero))),
            (14u, Int32Property(12)),
            (18u, StringProperty("Microsoft Word"))));
        WriteStream(root, "\u0005DocumentSummaryInformation", CreatePropertySet(
            DocumentSummaryInformationFormatId,
            (2u, StringProperty("Security")),
            (7u, Int32Property(4)),
            (14u, StringProperty("Morsa Manager")),
            (15u, StringProperty("Morsa Labs"))));

        var history = CreateRevisionTable("Historical Author", "C:\\Users\\history.user\\Documents\\previous.doc");
        var wordDocument = new byte[0x2da];
        wordDocument[0x0b] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(0x2d2, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wordDocument.AsSpan(0x2d6, 4), (uint)history.Length);
        WriteStream(root, "WordDocument", wordDocument);
        WriteStream(root, "1Table", history);

        var objectPool = root.CreateStorage("ObjectPool");
        WriteStream(objectPool, "\u0001Ole10Native", CreateOle10Native(
            "photo.jpg",
            "C:\\Users\\ole.user\\Pictures\\photo.jpg",
            "C:\\Temp\\photo.jpg",
            CreateExifJpeg("Embedded OLE Artist")));
        root.Flush();
        return path;
    }

    public string CreatePdf()
    {
        const string xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                       xmlns:dc="http://purl.org/dc/elements/1.1/"
                       xmlns:xmpMM="http://ns.adobe.com/xap/1.0/mm/"
                       xmlns:stEvt="http://ns.adobe.com/xap/1.0/sType/ResourceEvent#"
                       xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/">
                <rdf:Description>
                  <dc:creator><rdf:Seq><rdf:li>XMP Author</rdf:li></rdf:Seq></dc:creator>
                  <photoshop:DocumentAncestors><rdf:Bag><rdf:li>uuid:ancestor-1</rdf:li></rdf:Bag></photoshop:DocumentAncestors>
                  <xmpMM:History><rdf:Seq><rdf:li stEvt:action="saved" stEvt:when="2026-07-26T10:00:00Z" stEvt:softwareAgent="Adobe InDesign"/></rdf:Seq></xmpMM:History>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        var xmpCompressed = Compress(Encoding.UTF8.GetBytes(xmp));
        var embeddedFile = Compress(Encoding.UTF8.GetBytes("C:\\Users\\pdf.user\\Documents\\evidence.txt owner@example.test"));
        var jpeg = CreateExifJpeg("Embedded PDF Artist");

        using var output = new MemoryStream();
        WriteAscii("%PDF-1.7\n");
        WriteAscii("1 0 obj << /Title <FEFF004D006F00720073006100200055006E00690063006F00640065> /Author (Chris \\(PDF\\) Tester) /Producer (Morsa PDF Engine) >> endobj\n");
        WriteObjectStream("2 0 obj << /Type /Metadata /Subtype /XML /Filter /FlateDecode", xmpCompressed);
        WriteObjectStream("3 0 obj << /Type /XObject /Subtype /Image /Filter /DCTDecode", jpeg);
        WriteAscii("4 0 obj << /Type /Filespec /UF (evidence.txt) /EF << /F 5 0 R >> >> endobj\n");
        WriteObjectStream("5 0 obj << /Type /EmbeddedFile /Subtype /text#2Fplain /Filter /FlateDecode", embeddedFile);
        WriteAscii("%%EOF\n");
        return Write("sample.pdf", output.ToArray());

        void WriteObjectStream(string dictionary, byte[] content)
        {
            WriteAscii($"{dictionary} /Length {content.Length} >>\nstream\r\n");
            output.Write(content);
            WriteAscii("\r\nendstream\nendobj\n");
        }

        void WriteAscii(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }

    public string CreateMalformedOlePropertySet()
    {
        var path = Path.Combine(_root, "malformed.doc");
        using var root = RootStorage.Create(path);
        WriteStream(root, "\u0005SummaryInformation", [0xfe, 0xff, 0x00, 0x00]);
        root.Flush();
        return path;
    }

    public string Write(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static void WriteStream(Storage storage, string name, byte[] content)
    {
        using var stream = storage.CreateStream(name);
        stream.Write(content, 0, content.Length);
    }

    private static byte[] CreatePropertySet(byte[] formatId, params (uint Id, byte[] Value)[] properties)
    {
        const int sectionOffset = 48;
        var tableSize = 8 + (properties.Length * 8);
        var offsets = new int[properties.Length];
        var cursor = Align4(tableSize);
        for (var index = 0; index < properties.Length; index++)
        {
            offsets[index] = cursor;
            cursor = Align4(cursor + properties[index].Value.Length);
        }

        var sectionSize = cursor;
        var result = new byte[sectionOffset + sectionSize];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), 0xfffe);
        result[4] = 5; // Windows XP high/low version pair used by the legacy parser.
        result[5] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), 1);
        formatId.CopyTo(result, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), sectionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionOffset, 4), (uint)sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(sectionOffset + 4, 4), (uint)properties.Length);
        for (var index = 0; index < properties.Length; index++)
        {
            var table = sectionOffset + 8 + (index * 8);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(table, 4), properties[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(table + 4, 4), (uint)offsets[index]);
            properties[index].Value.CopyTo(result, sectionOffset + offsets[index]);
        }

        return result;
    }

    private static byte[] StringProperty(string value)
    {
        var encoded = Encoding.Unicode.GetBytes(value + "\0");
        var result = new byte[8 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0x1f);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(value.Length + 1));
        encoded.CopyTo(result, 8);
        return result;
    }

    private static byte[] Int16Property(short value)
    {
        var result = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0x02);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(4, 2), value);
        return result;
    }

    private static byte[] Int32Property(int value)
    {
        var result = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0x03);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), value);
        return result;
    }

    private static byte[] FileTimeProperty(DateTimeOffset value)
    {
        var result = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(4, 8), value.ToFileTime());
        return result;
    }

    private static byte[] CreateRevisionTable(string author, string path)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.Unicode, leaveOpen: true);
        writer.Write((ushort)0xffff);
        writer.Write((ushort)2);
        writer.Write((ushort)0);
        WriteString(author);
        WriteString(path);
        return output.ToArray();

        void WriteString(string value)
        {
            writer.Write((ushort)value.Length);
            writer.Write(Encoding.Unicode.GetBytes(value));
        }
    }

    private static byte[] CreateOle10Native(string label, string originalPath, string temporaryPath, byte[] payload)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.Latin1, leaveOpen: true);
        writer.Write(0u); // Patched after the envelope is complete.
        writer.Write((ushort)2);
        WriteCString(label);
        WriteCString(originalPath);
        writer.Write(0u);
        WriteCString(temporaryPath);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)(result.Length - 4));
        return result;

        void WriteCString(string value)
        {
            writer.Write(Encoding.Latin1.GetBytes(value));
            writer.Write((byte)0);
        }
    }

    private static byte[] CreateExifJpeg(string artist)
    {
        var artistBytes = Encoding.ASCII.GetBytes(artist + "\0");
        var tiff = new byte[26 + artistBytes.Length];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10, 2), 0x013b); // TIFF Artist.
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12, 2), 2); // ASCII.
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14, 4), (uint)artistBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18, 4), 26);
        artistBytes.CopyTo(tiff, 26);

        var exif = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(exif);
        tiff.CopyTo(exif, 6);
        var jpeg = new byte[2 + 2 + 2 + exif.Length + 2];
        jpeg[0] = 0xff;
        jpeg[1] = 0xd8;
        jpeg[2] = 0xff;
        jpeg[3] = 0xe1;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4, 2), checked((ushort)(exif.Length + 2)));
        exif.CopyTo(jpeg, 6);
        jpeg[^2] = 0xff;
        jpeg[^1] = 0xd9;
        return jpeg;
    }

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(content, 0, content.Length);
        return output.ToArray();
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
