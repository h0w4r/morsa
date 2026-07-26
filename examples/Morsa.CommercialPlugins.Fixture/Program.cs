using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Morsa.CommercialPlugins.Fixture;

/// <summary>Three-request loopback HTTP fixture used by JSONL smoke tests without provider traffic.</summary>
public static class Program
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length != 1 || !int.TryParse(arguments[0], out var port) || port is < 1024 or > 65535) return 2;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            for (var requestIndex = 0; requestIndex < 3; requestIndex++)
            {
                using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await HandleAsync(client).ConfigureAwait(false);
            }

            return 0;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleAsync(TcpClient client)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 8 * 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
        var contentLength = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { Length: > 0 } header)
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);
        }

        // Consume request content before replying so multipart upload is exercised end-to-end.
        var remaining = Math.Min(contentLength, 64 * 1024 * 1024);
        var buffer = new char[8 * 1024];
        while (remaining > 0)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0) break;
            remaining -= read;
        }

        var body = requestLine switch
        {
            var line when line.StartsWith("GET /api/v3/files/", StringComparison.Ordinal) =>
                "{\"data\":{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"type\":\"file\",\"attributes\":{\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size\":42,\"meaningful_name\":\"fixture.bin\",\"last_analysis_stats\":{\"malicious\":1,\"undetected\":69}}}}",
            var line when line.StartsWith("POST /api/v3/files ", StringComparison.Ordinal) =>
                "{\"data\":{\"id\":\"fixture-analysis\",\"type\":\"analysis\"}}",
            var line when line.StartsWith("GET /shodan/host/203.0.113.10", StringComparison.Ordinal) =>
                "{\"ip_str\":\"203.0.113.10\",\"org\":\"Fixture ISP\",\"ports\":[443],\"data\":[{\"port\":443,\"transport\":\"tcp\",\"product\":\"fixture-nginx\",\"data\":\"HTTP fixture banner\"}]}",
            _ => "{\"error\":{\"message\":\"fixture route not found\"}}",
        };
        var status = body.Contains("not found", StringComparison.Ordinal) ? "404 Not Found" : "200 OK";
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
