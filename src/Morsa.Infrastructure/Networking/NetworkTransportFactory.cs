using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using Morsa.Application.Abstractions;
using Morsa.Domain.Networking;

namespace Morsa.Infrastructure.Networking;

/// <summary>Creates credential-isolated HTTP handlers and raw proxy tunnels.</summary>
public sealed class NetworkTransportFactory(ISecretResolver secrets) : INetworkTransportFactory
{
    public HttpMessageHandler CreateHttpHandler(ProxyEndpoint? endpoint)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            MaxConnectionsPerServer = endpoint?.MaxConcurrency ?? 8,
        };

        if (endpoint is null)
        {
            handler.UseProxy = false;
            return handler;
        }

        var proxy = new WebProxy(ToTransportUri(endpoint));
        var credential = secrets.ResolveNetworkCredential(endpoint.SecretRef);
        if (credential is not null)
        {
            proxy.Credentials = credential;
        }

        handler.UseProxy = true;
        handler.Proxy = proxy;
        return handler;
    }

    public async Task<Stream> ConnectTcpAsync(
        ProxyEndpoint? endpoint,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        host = NormalizeAndValidateHost(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (endpoint is null)
        {
            var direct = new TcpClient();
            try
            {
                await direct.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return direct.GetStream();
            }
            catch
            {
                direct.Dispose();
                throw;
            }
        }

        var proxyUri = new Uri(endpoint.Uri);
        _ = NormalizeAndValidateHost(proxyUri.IdnHost);
        if (proxyUri.Port is < 1 or > 65535) throw new InvalidDataException("Proxy endpoint port is invalid.");
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(proxyUri.Host, proxyUri.Port, cancellationToken).ConfigureAwait(false);
            Stream stream = client.GetStream();

            if (endpoint.Protocol == ProxyProtocol.HttpsConnect)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = proxyUri.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, cancellationToken).ConfigureAwait(false);
                stream = ssl;
            }

            switch (endpoint.Protocol)
            {
                case ProxyProtocol.Http:
                case ProxyProtocol.HttpsConnect:
                    await NegotiateHttpConnectAsync(stream, endpoint, host, port, cancellationToken).ConfigureAwait(false);
                    break;
                case ProxyProtocol.Socks4:
                    await NegotiateSocks4Async(stream, endpoint, host, port, cancellationToken).ConfigureAwait(false);
                    break;
                case ProxyProtocol.Socks5:
                case ProxyProtocol.Socks5Host:
                    await NegotiateSocks5Async(stream, endpoint, host, port, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported proxy protocol: {endpoint.Protocol}.");
            }

            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task NegotiateHttpConnectAsync(
        Stream stream,
        ProxyEndpoint endpoint,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder()
            .Append("CONNECT ").Append(host).Append(':').Append(port).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n")
            .Append("Proxy-Connection: Keep-Alive\r\n");
        var credential = secrets.ResolveNetworkCredential(endpoint.SecretRef);
        if (credential is not null)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credential.UserName}:{credential.Password}"));
            builder.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }

        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
        var header = await ReadHeaderAsync(stream, 16 * 1024, cancellationToken).ConfigureAwait(false);
        var firstLine = header.Split("\r\n", 2, StringSplitOptions.None)[0];
        if (!firstLine.Contains(" 200 ", StringComparison.Ordinal))
        {
            throw new IOException($"HTTP proxy CONNECT failed: {firstLine}");
        }
    }

    private async Task NegotiateSocks4Async(
        Stream stream,
        ProxyEndpoint endpoint,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var credential = secrets.ResolveNetworkCredential(endpoint.SecretRef);
        var user = Encoding.UTF8.GetBytes(credential?.UserName ?? string.Empty);
        if (user.Length > 255) throw new InvalidOperationException("SOCKS4 user id exceeds 255 bytes.");
        var parsedAddress = IPAddress.TryParse(host, out var address);
        var remoteDns = endpoint.DnsMode == ProxyDnsMode.Remote;
        if (!remoteDns && !parsedAddress)
        {
            // SOCKS4 local-DNS mode is pinned to the address resolved here; SOCKS4a is only used explicitly.
            address = (await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork) ??
                throw new IOException("SOCKS4 local DNS did not return an IPv4 address.");
            parsedAddress = true;
        }
        var hostBytes = remoteDns ? Encoding.ASCII.GetBytes(host) : [];
        if (hostBytes.Length > 255) throw new ArgumentException("SOCKS4a hostname exceeds 255 bytes.", nameof(host));
        var request = new byte[9 + user.Length + (remoteDns ? hostBytes.Length + 1 : 0)];
        request[0] = 0x04;
        request[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), checked((ushort)port));
        (remoteDns ? new byte[] { 0, 0, 0, 1 } : address!.GetAddressBytes()).CopyTo(request, 4);
        user.CopyTo(request, 8);
        request[8 + user.Length] = 0;
        if (remoteDns)
        {
            hostBytes.CopyTo(request, 9 + user.Length);
        }

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = new byte[8];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        if (response[1] != 0x5a)
        {
            throw new IOException($"SOCKS4 proxy rejected CONNECT with code 0x{response[1]:x2}.");
        }
    }

    private async Task NegotiateSocks5Async(
        Stream stream,
        ProxyEndpoint endpoint,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var credential = secrets.ResolveNetworkCredential(endpoint.SecretRef);
        var greeting = credential is null ? new byte[] { 5, 1, 0 } : new byte[] { 5, 2, 0, 2 };
        await stream.WriteAsync(greeting, cancellationToken).ConfigureAwait(false);
        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, cancellationToken).ConfigureAwait(false);
        if (choice[0] != 5 || choice[1] is not (0 or 2))
        {
            throw new IOException("SOCKS5 proxy did not accept an authentication method.");
        }

        if (choice[1] == 2)
        {
            if (credential is null)
            {
                throw new IOException("SOCKS5 proxy requires credentials.");
            }

            var user = Encoding.UTF8.GetBytes(credential.UserName);
            var password = Encoding.UTF8.GetBytes(credential.Password);
            if (user.Length > 255 || password.Length > 255)
            {
                throw new InvalidOperationException("SOCKS5 credentials exceed protocol limits.");
            }

            var auth = new byte[3 + user.Length + password.Length];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)password.Length;
            password.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, cancellationToken).ConfigureAwait(false);
            var authResponse = new byte[2];
            await stream.ReadExactlyAsync(authResponse, cancellationToken).ConfigureAwait(false);
            if (authResponse[1] != 0)
            {
                throw new IOException("SOCKS5 username/password authentication failed.");
            }
        }

        var remoteDns = endpoint.DnsMode == ProxyDnsMode.Remote || endpoint.Protocol == ProxyProtocol.Socks5Host;
        byte addressType;
        byte[] addressBytes;
        if (!remoteDns)
        {
            var parsed = IPAddress.TryParse(host, out var literal)
                ? literal
                : (await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false)).FirstOrDefault() ??
                  throw new IOException("SOCKS5 local DNS returned no address.");
            addressBytes = parsed.GetAddressBytes();
            addressType = parsed.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        }
        else
        {
            addressBytes = Encoding.ASCII.GetBytes(host);
            if (addressBytes.Length > 255)
            {
                throw new ArgumentException("SOCKS5 hostname exceeds 255 bytes.", nameof(host));
            }

            addressType = 3;
        }

        var addressOffset = addressType == 3 ? 5 : 4;
        var request = new byte[addressOffset + addressBytes.Length + 2];
        request[0] = 5;
        request[1] = 1;
        request[2] = 0;
        request[3] = addressType;
        if (addressType == 3)
        {
            request[4] = (byte)addressBytes.Length;
        }

        addressBytes.CopyTo(request, addressOffset);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(addressOffset + addressBytes.Length, 2), checked((ushort)port));
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        var responseHead = new byte[4];
        await stream.ReadExactlyAsync(responseHead, cancellationToken).ConfigureAwait(false);
        if (responseHead[0] != 5 || responseHead[1] != 0)
        {
            throw new IOException($"SOCKS5 proxy rejected CONNECT with code 0x{responseHead[1]:x2}.");
        }

        var remaining = responseHead[3] switch
        {
            1 => 4 + 2,
            4 => 16 + 2,
            3 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false) + 2,
            _ => throw new IOException("SOCKS5 proxy returned an invalid address type."),
        };
        var discard = new byte[remaining];
        await stream.ReadExactlyAsync(discard, cancellationToken).ConfigureAwait(false);
    }

    private static Uri ToTransportUri(ProxyEndpoint endpoint)
    {
        var original = new Uri(endpoint.Uri);
        var scheme = endpoint.Protocol switch
        {
            ProxyProtocol.Http => "http",
            ProxyProtocol.HttpsConnect => "https",
            ProxyProtocol.Socks4 => "socks4",
            ProxyProtocol.Socks5 => "socks5",
            ProxyProtocol.Socks5Host => "socks5",
            _ => throw new NotSupportedException(),
        };
        return new UriBuilder(original) { Scheme = scheme }.Uri;
    }

    private static string NormalizeAndValidateHost(string host)
    {
        host = host.Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        if (host.Length is 0 or > 255 || host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("Destination host is empty, oversized or contains forbidden characters.", nameof(host));
        return host;
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        await stream.ReadExactlyAsync(value, cancellationToken).ConfigureAwait(false);
        return value[0];
    }

    private static async Task<string> ReadHeaderAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        while (buffer.Count < maximumBytes)
        {
            await stream.ReadExactlyAsync(single, cancellationToken).ConfigureAwait(false);
            buffer.Add(single[0]);
            if (buffer.Count >= 4 &&
                buffer[^4] == '\r' && buffer[^3] == '\n' &&
                buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(buffer));
            }
        }

        throw new IOException("Proxy response header exceeded the configured limit.");
    }
}
