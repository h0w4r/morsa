using System.Buffers.Binary;
using System.Net;
using System.Text;
using DnsClient;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;
using Morsa.Domain.Recon;

namespace Morsa.Infrastructure.Recon;

/// <summary>Minimal bounded DNS-over-TCP codec used exclusively through an explicit proxy tunnel.</summary>
public sealed class SocksDnsClient(INetworkTransportFactory transports)
{
    public async Task<IReadOnlyList<DnsObservation>> QueryAsync(
        Guid runId,
        string name,
        QueryType type,
        ProxyEndpoint endpoint,
        string resolver,
        CancellationToken cancellationToken)
    {
        if (endpoint.Protocol is not (ProxyProtocol.Socks5 or ProxyProtocol.Socks5Host) || endpoint.DnsMode != ProxyDnsMode.Remote)
            throw new InvalidOperationException("Remote DNS requires a SOCKS5 endpoint with dns_mode=remote.");
        var request = BuildQuery(name, type);
        await using var stream = await transports.ConnectTcpAsync(endpoint, resolver, 53, cancellationToken).ConfigureAwait(false);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)request.Length));
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(length);
        if (responseLength is < 12 or > 65_535) throw new InvalidDataException("DNS response length is invalid.");
        var response = new byte[responseLength];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResponse(runId, response, endpoint.Id);
    }

    private static byte[] BuildQuery(string name, QueryType type)
    {
        using var stream = new MemoryStream(512);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)Random.Shared.Next(1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // Recursion desired.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new InvalidDataException("DNS label length is invalid.");
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, checked((ushort)type));
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
        stream.Write(tail);
        return stream.ToArray();
    }

    private static IReadOnlyList<DnsObservation> ParseResponse(Guid runId, byte[] message, Guid endpointId)
    {
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2, 2));
        if ((flags & 0x8000) == 0) throw new InvalidDataException("DNS packet is not a response.");
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6, 2));
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(8, 2));
        var additionalCount = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10, 2));
        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            _ = ReadName(message, ref offset);
            EnsureAvailable(message, offset, 4);
            offset += 4;
        }
        var records = new List<DnsObservation>();
        var total = Math.Min(answerCount + authorityCount + additionalCount, 10_000);
        for (var index = 0; index < total; index++)
        {
            var owner = ReadName(message, ref offset);
            EnsureAvailable(message, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset, 2));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4, 4));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8, 2));
            offset += 10;
            EnsureAvailable(message, offset, length);
            var rdataOffset = offset;
            var value = ParseRData(message, type, rdataOffset, length);
            offset += length;
            records.Add(new DnsObservation
            {
                RunId = runId,
                Name = owner,
                RecordType = ((QueryType)type).ToString(),
                Value = value,
                Ttl = ttl,
                Source = $"socks-dns:{endpointId:N}",
            });
        }
        return records;
    }

    private static string ParseRData(byte[] message, ushort type, int offset, int length)
    {
        var span = message.AsSpan(offset, length);
        return (QueryType)type switch
        {
            QueryType.A when length == 4 => new IPAddress(span).ToString(),
            QueryType.AAAA when length == 16 => new IPAddress(span).ToString(),
            QueryType.NS or QueryType.CNAME or QueryType.PTR => ReadNameAt(message, offset),
            QueryType.MX when length >= 3 => $"{BinaryPrimitives.ReadUInt16BigEndian(span)} {ReadNameAt(message, offset + 2)}",
            QueryType.SRV when length >= 7 => $"{BinaryPrimitives.ReadUInt16BigEndian(span)} {BinaryPrimitives.ReadUInt16BigEndian(span[2..])} {BinaryPrimitives.ReadUInt16BigEndian(span[4..])} {ReadNameAt(message, offset + 6)}",
            QueryType.TXT => ParseText(span),
            QueryType.CAA when length >= 2 => $"{span[0]} {Encoding.ASCII.GetString(span.Slice(2, Math.Min(span[1], length - 2)))} {Encoding.UTF8.GetString(span[(2 + Math.Min(span[1], length - 2))..])}",
            QueryType.SOA => ParseSoa(message, offset, length),
            _ => Convert.ToHexString(span).ToLowerInvariant(),
        };
    }

    private static string ParseSoa(byte[] message, int offset, int length)
    {
        var end = offset + length;
        var primary = ReadName(message, ref offset);
        var mailbox = ReadName(message, ref offset);
        EnsureAvailable(message, offset, 20);
        if (offset + 20 > end) throw new InvalidDataException("SOA RDATA is truncated.");
        return $"{primary} {mailbox} {BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset, 4))} {BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4, 4))} {BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 8, 4))} {BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 12, 4))} {BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 16, 4))}";
    }

    private static string ParseText(ReadOnlySpan<byte> value)
    {
        var parts = new List<string>();
        for (var offset = 0; offset < value.Length;)
        {
            var length = value[offset++];
            if (offset + length > value.Length) throw new InvalidDataException("TXT RDATA is truncated.");
            parts.Add(Encoding.UTF8.GetString(value.Slice(offset, length)));
            offset += length;
        }
        return string.Join(' ', parts);
    }

    private static string ReadNameAt(byte[] message, int offset) => ReadName(message, ref offset);

    private static string ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        for (var depth = 0; depth < 128; depth++)
        {
            EnsureAvailable(message, cursor, 1);
            var length = message[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                return string.Join('.', labels);
            }
            if ((length & 0xc0) == 0xc0)
            {
                EnsureAvailable(message, cursor, 1);
                var pointer = ((length & 0x3f) << 8) | message[cursor++];
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }
            if (length > 63) throw new InvalidDataException("DNS label length is invalid.");
            EnsureAvailable(message, cursor, length);
            labels.Add(Encoding.ASCII.GetString(message, cursor, length));
            cursor += length;
            if (!jumped) offset = cursor;
        }
        throw new InvalidDataException("DNS compression pointer depth exceeded.");
    }

    private static void EnsureAvailable(byte[] message, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > message.Length) throw new InvalidDataException("DNS message is truncated.");
    }
}
