﻿using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NexNet.Transports;

namespace NexNetDemo.Samples.Messenger;

public class MessengerSample : SampleBase
{
    public MessengerSample(string serverIpAddress)
        : base(false, TransportMode.TlsTcp)
    {
        ServerConfig = new TcpTlsServerConfig()
        {
            EndPoint = new IPEndPoint(IPAddress.Parse(serverIpAddress), 4236),
            Logger = new SampleLogger("Server"),
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
            EndPoint = new IPEndPoint(IPAddress.Parse(serverIpAddress), 4236),
            Logger = new SampleLogger("Client"),
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
        var server = MessengerSampleServerNexus.CreateServer(ServerConfig, () => new MessengerSampleServerNexus());
        server.Start();

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
        var client = MessengerSampleClientNexus.CreateClient(ClientConfig, new MessengerSampleClientNexus());
        await client.ConnectAsync(true);
    }


}
