using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Logging;
using NexNet.Quic;
using NexNet.Transports;

namespace NexNetDemo.Samples;

public class SampleBase
{
    public enum TransportMode
    {
        Uds,
        Tcp,
        TlsTcp,
        Quic
    }

    protected ServerConfig ServerConfig { get; set; } = null!;
    protected ClientConfig ClientConfig { get; set; } = null!;

    public ConsoleLogger? Logger { get; private set; } 

    public SampleBase(bool log, TransportMode transportMode)
    {
        ConsoleLogger? logger;
        if (log)
        {
            logger = new ConsoleLogger
            {
                MinLogLevel = NexusLogLevel.Information
            };
        }
        else
        {
            logger = null;
        }

        
        
        if (transportMode == TransportMode.Uds)
        {
            var path = "test.sock";
            if (File.Exists(path))
                File.Delete(path);

            ServerConfig = new UdsServerConfig()
            {
                EndPoint = new UnixDomainSocketEndPoint(path), 
                Logger = logger?.CreatePrefixedLogger(null, "Server")
            };
            ClientConfig = new UdsClientConfig()
            {
                EndPoint = new UnixDomainSocketEndPoint(path),
                Logger = logger?.CreatePrefixedLogger(null, "Client")
            };
        }
        else if (transportMode == TransportMode.Tcp)
        {
            ServerConfig = new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = logger?.CreatePrefixedLogger(null, "Server")
            };
            ClientConfig = new TcpClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = logger?.CreatePrefixedLogger(null, "Client")
            };
        }
        else if (transportMode == TransportMode.TlsTcp)
        {
            ServerConfig = new TcpTlsServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = logger?.CreatePrefixedLogger(null, "Server"),
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = new X509Certificate2("server.pfx", "certPass"),
                },
            };
            ClientConfig = new TcpTlsClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = logger?.CreatePrefixedLogger(null, "Client"),
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
                }
            };
        }       
        else if (transportMode == TransportMode.Quic)
        {

            ServerConfig = new QuicServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 6321),
                Logger = logger?.CreatePrefixedLogger(null, "Server"),
                SslServerAuthenticationOptions = new SslServerAuthenticationOptions()
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    AllowRenegotiation = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ServerCertificate = new X509Certificate2("server.pfx", "certPass"),
                },
            };
            ClientConfig = new QuicClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 6321),
                Logger = logger?.CreatePrefixedLogger(null, "Client"),
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                {
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }
            };
        }
    }
}
