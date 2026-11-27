using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.Inspector.Sample;

/// <summary>Transports the sample serves and connects over.</summary>
public enum SampleTransport : byte
{
    Tcp = 0,
    Tls = 1,
    Uds = 2,
}

/// <summary>Where a server listens: a TCP port (plain or TLS) or a Unix domain socket path.</summary>
/// <param name="Transport">The transport.</param>
/// <param name="Host">Address to bind or connect to (TCP and TLS).</param>
/// <param name="Port">Port (TCP and TLS).</param>
/// <param name="Path">Socket path (UDS).</param>
public readonly record struct SampleAddress(SampleTransport Transport, IPAddress Host, int Port, string? Path)
{
    /// <summary>
    /// A loopback address: TCP and TLS ask for port 0, so the operating system picks a free port when the server binds
    /// (see <see cref="BoundTo"/>); a port probed free beforehand could be taken by another process before the bind.
    /// </summary>
    public static SampleAddress Loopback(SampleTransport transport) =>
        new(transport, IPAddress.Loopback, 0,
            transport == SampleTransport.Uds ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nnchat-{Guid.NewGuid():N}.sock") : null);

    /// <summary>
    /// Parses an endpoint written as <c>tcp://HOST:PORT</c>, <c>tls://HOST:PORT</c> or <c>uds:PATH</c> (the forms the
    /// inspector endpoint options use).
    /// </summary>
    public static bool TryParse(string text, out SampleAddress address)
    {
        address = default;
        SampleTransport transport;
        string rest;
        if (text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            (transport, rest) = (SampleTransport.Tcp, text[6..]);
        else if (text.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            (transport, rest) = (SampleTransport.Tls, text[6..]);
        else if (text.StartsWith("uds:", StringComparison.OrdinalIgnoreCase))
        {
            address = new SampleAddress(SampleTransport.Uds, IPAddress.Loopback, 0, text[4..]);
            return address.Path!.Length > 0;
        }
        else
            return false;

        if (!IPEndPoint.TryParse(rest, out var endPoint) || endPoint.Port == 0)
            return false;
        address = new SampleAddress(transport, endPoint.Address, endPoint.Port, null);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Transport switch
    {
        SampleTransport.Uds => "uds:" + Path,
        SampleTransport.Tls => $"tls://{Host}:{Port}",
        _ => $"tcp://{Host}:{Port}",
    };

    /// <summary>A server configuration listening here.</summary>
    /// <param name="certificate">The TLS certificate; required for <see cref="SampleTransport.Tls"/>.</param>
    public ServerConfig CreateServerConfig(X509Certificate2? certificate = null)
    {
        ServerConfig config = Transport switch
        {
            SampleTransport.Uds => new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(Path!) },
            SampleTransport.Tcp => new TcpServerConfig { EndPoint = new IPEndPoint(Host, Port), TcpNoDelay = true },
            _ => new TcpTlsServerConfig
            {
                EndPoint = new IPEndPoint(Host, Port),
                TcpNoDelay = true,
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate ?? throw new ArgumentNullException(nameof(certificate)),
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
            },
        };
        return config;
    }

    /// <summary>A client configuration connecting here. TLS accepts any server certificate: the sample serves a self-signed one.</summary>
    public ClientConfig CreateClientConfig()
    {
        ClientConfig config = Transport switch
        {
            SampleTransport.Uds => new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(Path!) },
            SampleTransport.Tcp => new TcpClientConfig { EndPoint = new IPEndPoint(Host, Port), TcpNoDelay = true },
            _ => new TcpTlsClientConfig
            {
                EndPoint = new IPEndPoint(Host, Port),
                TcpNoDelay = true,
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
            },
        };
        return config;
    }

    /// <summary>Deletes the socket file of a UDS address.</summary>
    public void Cleanup()
    {
        if (Transport != SampleTransport.Uds || Path == null)
            return;
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Still held by a closing listener; the temp directory is cleaned eventually.
        }
    }

    /// <summary>This address with the port <paramref name="server"/> is bound to (the picked one for port 0); a client connects there.</summary>
    /// <param name="server">The started server listening on this address.</param>
    public SampleAddress BoundTo(INexusServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (Transport == SampleTransport.Uds)
            return this;
        return server.Endpoints[0].BoundEndPoint is IPEndPoint bound
            ? this with { Port = bound.Port }
            : throw new InvalidOperationException("The server reported no bound TCP end point.");
    }

    /// <summary>Points a TCP or TLS client configuration at this address (UDS: unchanged).</summary>
    /// <param name="config">A configuration made by <see cref="CreateClientConfig"/>.</param>
    public void Apply(ClientConfig config)
    {
        if (config is TcpClientConfig tcp)
            tcp.EndPoint = new IPEndPoint(Host, Port);
    }
}

/// <summary>TLS material for the sample.</summary>
public static class SampleCertificates
{
    /// <summary>
    /// Loads a PKCS#12 file, or creates a self-signed certificate for <c>localhost</c> / <c>127.0.0.1</c>. The
    /// self-signed key round-trips through PKCS#12 because Windows' TLS stack cannot use an ephemeral key directly.
    /// </summary>
    public static X509Certificate2 Load(string? pfxPath, string? password)
    {
        if (pfxPath != null)
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
