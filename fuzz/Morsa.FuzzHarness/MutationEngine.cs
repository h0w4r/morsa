using System.Globalization;
using System.Text;

namespace Morsa.FuzzHarness;

/// <summary>Motor mutacional determinista sin dependencias externas.</summary>
internal sealed class MutationEngine(int seed, IReadOnlyList<byte[]> dictionary, int maximumBytes)
{
    private readonly Random _random = new(seed);
    private readonly IReadOnlyList<byte[]> _dictionary = dictionary;
    private readonly int _maximumBytes = maximumBytes;

    public byte[] Mutate(byte[] seedInput, IReadOnlyList<byte[]> allSeeds)
    {
        var data = seedInput.Take(_maximumBytes).ToList();
        var operations = _random.Next(1, 9);
        for (var operation = 0; operation < operations; operation++)
        {
            switch (_random.Next(9))
            {
                case 0:
                    FlipBit(data);
                    break;
                case 1:
                    ReplaceByte(data);
                    break;
                case 2:
                    DeleteRange(data);
                    break;
                case 3:
                    DuplicateRange(data);
                    break;
                case 4:
                    InsertDictionaryToken(data);
                    break;
                case 5:
                    OverwriteInteger(data);
                    break;
                case 6:
                    Splice(data, allSeeds);
                    break;
                case 7:
                    Truncate(data);
                    break;
                default:
                    InsertRandomBytes(data);
                    break;
            }
        }

        return data.Take(_maximumBytes).ToArray();
    }

    public static IReadOnlyList<byte[]> LoadDictionary(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        var tokens = new List<byte[]>();
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            var quoted = equals >= 0 ? line[(equals + 1)..].Trim() : line;
            if (quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"')
            {
                quoted = quoted[1..^1];
            }

            var decoded = DecodeEscapes(quoted);
            if (decoded.Length > 0)
            {
                tokens.Add(decoded);
            }
        }

        return tokens;
    }

    private static byte[] DecodeEscapes(string value)
    {
        using var stream = new MemoryStream();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                WriteUtf8(stream, value[index].ToString());
                continue;
            }

            var escape = value[++index];
            switch (escape)
            {
                case 'n':
                    stream.WriteByte((byte)'\n');
                    break;
                case 'r':
                    stream.WriteByte((byte)'\r');
                    break;
                case 't':
                    stream.WriteByte((byte)'\t');
                    break;
                case '\\':
                case '"':
                    stream.WriteByte((byte)escape);
                    break;
                case 'x' when index + 2 < value.Length &&
                                   byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                                       CultureInfo.InvariantCulture, out var parsed):
                    stream.WriteByte(parsed);
                    index += 2;
                    break;
                default:
                    WriteUtf8(stream, escape.ToString());
                    break;
            }
        }

        return stream.ToArray();
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private void FlipBit(List<byte> data)
    {
        EnsureNonEmpty(data);
        var index = _random.Next(data.Count);
        data[index] ^= (byte)(1 << _random.Next(8));
    }

    private void ReplaceByte(List<byte> data)
    {
        EnsureNonEmpty(data);
        data[_random.Next(data.Count)] = (byte)_random.Next(256);
    }

    private void DeleteRange(List<byte> data)
    {
        if (data.Count == 0)
        {
            return;
        }

        var start = _random.Next(data.Count);
        var count = Math.Min(data.Count - start, _random.Next(1, Math.Min(256, data.Count - start) + 1));
        data.RemoveRange(start, count);
    }

    private void DuplicateRange(List<byte> data)
    {
        if (data.Count == 0 || data.Count >= _maximumBytes)
        {
            return;
        }

        var start = _random.Next(data.Count);
        var count = Math.Min(data.Count - start, _random.Next(1, Math.Min(512, data.Count - start) + 1));
        var copy = data.GetRange(start, Math.Min(count, _maximumBytes - data.Count));
        data.InsertRange(_random.Next(data.Count + 1), copy);
    }

    private void InsertDictionaryToken(List<byte> data)
    {
        if (_dictionary.Count == 0 || data.Count >= _maximumBytes)
        {
            return;
        }

        var token = _dictionary[_random.Next(_dictionary.Count)];
        data.InsertRange(_random.Next(data.Count + 1), token.Take(_maximumBytes - data.Count));
    }

    private void OverwriteInteger(List<byte> data)
    {
        EnsureNonEmpty(data);
        var interesting = new[] { 0, 1, -1, int.MaxValue, int.MinValue, 0x7fff, 0x10000 };
        var bytes = BitConverter.GetBytes(interesting[_random.Next(interesting.Length)]);
        var offset = _random.Next(data.Count);
        for (var index = 0; index < bytes.Length && offset + index < data.Count; index++)
        {
            data[offset + index] = bytes[index];
        }
    }

    private void Splice(List<byte> data, IReadOnlyList<byte[]> seeds)
    {
        if (seeds.Count == 0 || data.Count >= _maximumBytes)
        {
            return;
        }

        var donor = seeds[_random.Next(seeds.Count)];
        if (donor.Length == 0)
        {
            return;
        }

        var start = _random.Next(donor.Length);
        var count = Math.Min(donor.Length - start, Math.Min(1_024, _maximumBytes - data.Count));
        data.InsertRange(_random.Next(data.Count + 1), donor.AsSpan(start, count).ToArray());
    }

    private void Truncate(List<byte> data)
    {
        if (data.Count > 0)
        {
            var newLength = _random.Next(data.Count);
            data.RemoveRange(newLength, data.Count - newLength);
        }
    }

    private void InsertRandomBytes(List<byte> data)
    {
        if (data.Count >= _maximumBytes)
        {
            return;
        }

        var count = Math.Min(_random.Next(1, 65), _maximumBytes - data.Count);
        var bytes = new byte[count];
        _random.NextBytes(bytes);
        data.InsertRange(_random.Next(data.Count + 1), bytes);
    }

    private static void EnsureNonEmpty(List<byte> data)
    {
        if (data.Count == 0)
        {
            data.Add(0);
        }
    }
}
