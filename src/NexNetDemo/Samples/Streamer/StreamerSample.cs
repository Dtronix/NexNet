using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Quic;
using NexNet.Transports;

namespace NexNetDemo.Samples.Messenger;

public class StreamerSample : SampleBase
{
    public StreamerSample(string serverIpAddress)
        : base(false, TransportMode.TlsTcp)
    {
        ServerConfig = new QuicServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Parse(serverIpAddress), 4236),
            //Logger = new SampleLogger("Server"),
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
            EndPoint = new IPEndPoint(IPAddress.Parse(serverIpAddress), 4236),
            //Logger = new SampleLogger("Client"),
            SslClientAuthenticationOptions = new SslClientAuthenticationOptions()
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
                RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true
            }
        };
    }

    public async Task RunServer()
    {
        var server = StreamerSampleServerNexus.CreateServer(ServerConfig, () => new StreamerSampleServerNexus());
        await server.StartAsync();

        if(server.StoppedTask != null)
            await server.StoppedTask;
    }

    public async Task RunClient()
    {
        ClientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new TimeSpan[]
        {
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
        }, true);
        var client = StreamerSampleClientNexus.CreateClient(ClientConfig, new StreamerSampleClientNexus());
        await client.ConnectAsync();
    }


}
