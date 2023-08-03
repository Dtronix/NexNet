using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Transports;

namespace NexNetDemo.Samples;

public class SampleBase
{
    public enum TransportMode
    {
        Uds,
        Tcp,
        TlsTcp
    }

    protected ServerConfig ServerConfig { get; set; } = null!;
    protected ClientConfig ClientConfig { get; set; } = null!;

    public SampleBase(bool log, TransportMode transportMode)
    {
        if (transportMode == TransportMode.Uds)
        {
            var path = "test.sock";
            if (File.Exists(path))
                File.Delete(path);

            ServerConfig = new UdsServerConfig()
            {
                EndPoint = new UnixDomainSocketEndPoint(path), 
                Logger = log ? new SampleLogger("Server") : null
            };
            ClientConfig = new UdsClientConfig()
            {
                EndPoint = new UnixDomainSocketEndPoint(path),
                Logger = log ? new SampleLogger("Client") : null
            };
        }
        else if (transportMode == TransportMode.Tcp)
        {
            ServerConfig = new TcpServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = log ? new SampleLogger("Server") : null
            };
            ClientConfig = new TcpClientConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = log ? new SampleLogger("Client") : null
            };
        }
        else if (transportMode == TransportMode.TlsTcp)
        {
            ServerConfig = new TcpTlsServerConfig()
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 1236),
                Logger = log ? new SampleLogger("Server") : null,
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
                Logger = log ? new SampleLogger("Client") : null,
                SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                    RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
                }
            };
        }
    }
}
