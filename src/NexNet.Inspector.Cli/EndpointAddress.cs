using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Cli;

/// <summary>
/// Where an inspector endpoint listens: <c>tcp://host:port</c>, <c>tls://host:port</c> or <c>uds:/path</c>
/// (on Windows also <c>uds:C:\path</c>).
/// </summary>
/// <param name="Scheme">The transport.</param>
/// <param name="Host">Host name or address for TCP and TLS; the socket path for UDS.</param>
/// <param name="Port">The TCP port; 0 for UDS.</param>
internal readonly record struct EndpointAddress(EndpointScheme Scheme, string Host, int Port)
{
    public static bool TryParse(string text, out EndpointAddress address, [NotNullWhen(false)] out string? error)
    {
        address = default;
        error = null;
        var colon = text.IndexOf(':');
        var scheme = colon <= 0 ? null : text[..colon] switch
        {
            "tcp" => EndpointScheme.Tcp,
            "tls" => EndpointScheme.Tls,
            "uds" => (EndpointScheme?)EndpointScheme.Uds,
            _ => null,
        };
        if (scheme is null)
        {
            error = $"Endpoint '{text}' must start with tcp://, tls:// or uds:.";
            return false;
        }

        var rest = text.AsSpan(colon + 1);
        if (scheme == EndpointScheme.Uds)
        {
            // uds:/path, uds:C:\path; the uds:// form is accepted too, with the path after the slashes.
            if (rest.StartsWith("//"))
                rest = rest[2..];
            if (rest.IsEmpty)
            {
                error = $"Endpoint '{text}' names no socket path.";
                return false;
            }

            address = new EndpointAddress(EndpointScheme.Uds, rest.ToString(), 0);
            return true;
        }

        if (!rest.StartsWith("//"))
        {
            error = $"Endpoint '{text}' must have the form {text[..colon]}://host:port.";
            return false;
        }

        rest = rest[2..];
        if (rest.EndsWith("/"))
            rest = rest[..^1];

        // The port follows the last colon; an IPv6 host is bracketed ([::1]:7000) so its own colons stay inside.
        var portColon = rest.LastIndexOf(':');
        if (portColon <= 0 || (rest.Contains(']') && rest.LastIndexOf(']') > portColon))
        {
            error = $"Endpoint '{text}' has no port.";
            return false;
        }

        var host = rest[..portColon];
        if (host.StartsWith("[") && host.EndsWith("]"))
            host = host[1..^1];
        if (host.IsEmpty
            || !int.TryParse(rest[(portColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            error = $"Endpoint '{text}' needs a host and a port between 1 and 65535.";
            return false;
        }

        address = new EndpointAddress(scheme.Value, host.ToString(), port);
        return true;
    }

    /// <summary>Builds the client configuration, resolving a host name to its first address.</summary>
    /// <param name="insecure">For TLS: accept any server certificate (self-signed development certificates).</param>
    /// <param name="serverName">For TLS: the name the certificate must carry; the host by default.</param>
    /// <param name="connectTimeout">Bound on the TCP connect and the TLS handshake.</param>
    /// <param name="cancellationToken">Cancels name resolution.</param>
    public async Task<ClientConfig> CreateClientConfigAsync(bool insecure, string? serverName, TimeSpan connectTimeout,
        CancellationToken cancellationToken)
    {
        ClientConfig config;
        if (Scheme == EndpointScheme.Uds)
        {
            config = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(Host) };
        }
        else
        {
            var endPoint = new IPEndPoint(await ResolveAsync(Host, cancellationToken).ConfigureAwait(false), Port);
            if (Scheme == EndpointScheme.Tcp)
            {
                config = new TcpClientConfig { EndPoint = endPoint };
            }
            else
            {
                var tls = new SslClientAuthenticationOptions
                {
                    TargetHost = serverName ?? Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                };
                if (insecure)
                {
                    // A development aid: an endpoint behind a self-signed certificate cannot be validated otherwise.
                    tls.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
                    tls.CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                }

                config = new TcpTlsClientConfig
                {
                    EndPoint = endPoint,
                    SslClientAuthenticationOptions = tls,
                    SslConnectionTimeout = ToMilliseconds(connectTimeout),
                };
            }
        }

        config.ConnectionTimeout = ToMilliseconds(connectTimeout);
        return config;
    }

    public override string ToString() => Scheme switch
    {
        EndpointScheme.Uds => "uds:" + Host,
        EndpointScheme.Tls => $"tls://{FormatHost(Host)}:{Port}",
        _ => $"tcp://{FormatHost(Host)}:{Port}",
    };

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static int ToMilliseconds(TimeSpan span) => (int)Math.Clamp(span.TotalMilliseconds, 1, int.MaxValue);

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
            return address;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        // Prefer IPv4: "localhost" often resolves to ::1 first while development servers bind 127.0.0.1.
        foreach (var candidate in addresses)
        {
            if (candidate.AddressFamily == AddressFamily.InterNetwork)
                return candidate;
        }

        return addresses.Length > 0
            ? addresses[0]
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}
