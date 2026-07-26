using System.IO.Compression;
using System.Text;

namespace Morsa.UnitTests;

/// <summary>Builds small, deterministic artifacts so parser tests never depend on external files.</summary>
internal sealed class SyntheticCorpus : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-corpus", Guid.NewGuid().ToString("N"));

    public SyntheticCorpus() => Directory.CreateDirectory(_root);

    public string CreateText(string name, string content)
    {
        var path = Path.Combine(_root, name);
        // A BOM would hide magic bytes such as %PDF- from the format inspector.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public string CreateBinary(string name, ReadOnlySpan<byte> content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content.ToArray());
        return path;
    }

    public string CreateZip(string name, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Content);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
