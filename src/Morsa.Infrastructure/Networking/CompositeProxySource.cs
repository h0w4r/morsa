using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Morsa.Application.Abstractions;
using Morsa.Application.Services;

namespace Morsa.Infrastructure.Networking;

/// <summary>Loads proxy candidates from file, stdin, HTTPS, environment or an explicit JSONL command.</summary>
public sealed class CompositeProxySource
{
    public async IAsyncEnumerable<ProxyCandidate> LoadAsync(
        string source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (source == "-")
        {
            await foreach (var candidate in ParseLinesAsync(Console.In, cancellationToken).ConfigureAwait(false)) yield return candidate;
            yield break;
        }

        if (source.Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" })
            {
                var value = Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name.ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(value)) yield return FileProxySource.Parse(value, null, 1, [name.ToLowerInvariant()]);
            }
            yield break;
        }

        // `command:...` is itself a syntactically valid absolute URI. Handle the explicit
        // source prefix before generic URI parsing or this provider can never be reached.
        if (source.StartsWith("command:", StringComparison.OrdinalIgnoreCase))
        {
            var command = source["command:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidDataException("Command proxy source is empty.");
            var start = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Proxy source command could not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var stderrTask = DrainBoundedAsync(process.StandardError, 256 * 1024, deadline.Token);
            try
            {
                await foreach (var candidate in ParseLinesAsync(process.StandardOutput, deadline.Token).ConfigureAwait(false)) yield return candidate;
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException($"Proxy source command exited with {process.ExitCode}.");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            yield break;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https") throw new InvalidDataException("Remote proxy sources must use HTTPS.");
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Remote proxy source URLs must not contain inline credentials.");
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(ScopePolicy.IsPrivate))
                throw new InvalidDataException("Remote proxy source resolves to a protected address class.");
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseProxy = false,
                // Pin the validation answers so DNS rebinding cannot redirect the subsequent socket.
                ConnectCallback = (context, token) => ConnectPinnedAsync(context, uri.IdnHost, addresses, token),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bounded = new StreamReader(new BoundedReadStream(stream, 2 * 1024 * 1024), Encoding.UTF8, true, 16 * 1024, leaveOpen: false);
            await foreach (var candidate in ParseLinesAsync(bounded, cancellationToken).ConfigureAwait(false)) yield return candidate;
            yield break;
        }

        var file = new FileProxySource(Path.GetFullPath(source));
        await foreach (var candidate in file.LoadAsync(cancellationToken).ConfigureAwait(false)) yield return candidate;
    }

    private static async IAsyncEnumerable<ProxyCandidate> ParseLinesAsync(
        TextReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? line;
        var records = 0;
        long characters = 0;
        while ((line = await ReadBoundedLineAsync(reader, 1024 * 1024, cancellationToken).ConfigureAwait(false)) is not null)
        {
            characters = checked(characters + line.Length);
            if (++records > 100_000 || characters > 16L * 1024 * 1024)
                throw new InvalidDataException("Proxy source exceeds the record or text budget.");
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(value);
                var root = document.RootElement;
                var uri = root.GetProperty("uri").GetString() ?? throw new InvalidDataException("Proxy JSONL record omits uri.");
                var secret = root.TryGetProperty("secret_ref", out var secretNode) ? secretNode.GetString() : null;
                var weight = root.TryGetProperty("weight", out var weightNode) ? weightNode.GetInt32() : 1;
                var tags = root.TryGetProperty("tags", out var tagsNode)
                    ? tagsNode.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : [];
                yield return FileProxySource.Parse(uri, secret, weight, tags);
            }
            else
            {
                yield return FileProxySource.Parse(value, null, 1, []);
            }
        }
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(
        SocketsHttpConnectionContext context,
        string allowedHost,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        if (!context.DnsEndPoint.Host.Equals(allowedHost, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Remote source transport attempted an unvalidated host.");
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = exception;
                if (cancellationToken.IsCancellationRequested) throw;
            }
        }
        throw new HttpRequestException("Remote source DNS addresses did not accept the connection.", last);
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var single = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(single.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (single[0] == '\n') return builder.ToString().TrimEnd('\r');
            builder.Append(single[0]);
            if (builder.Length > maximumCharacters) throw new InvalidDataException("Proxy source line exceeds 1 MiB.");
        }
    }

    private static async Task<string> DrainBoundedAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.ToString();
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    /// <summary>Stops remote sources from streaming beyond the configured cache budget.</summary>
    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _read = checked(_read + read);
            if (_read > maximum) throw new InvalidDataException("Remote proxy source exceeds 2 MiB.");
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
