using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using NexNet;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNetBenchmarks;

public enum BenchTransport
{
    Uds,
    Tcp,
    TcpTls,
    Quic,
}

/// <summary>
/// A connected client and server over UDS, loopback TCP, TCP with TLS 1.3 or QUIC, optionally (plain TCP only) with
/// the client connecting through a <see cref="LatencyProxy"/>.
/// </summary>
internal sealed class BenchConnection : IAsyncDisposable
{
    private LatencyProxy? _proxy;

    public NexusClient<ClientNexus, ClientNexus.ServerProxy> Client { get; private set; } = null!;
    public NexusServer<ServerNexus, ServerNexus.ClientProxy> Server { get; private set; } = null!;

    /// <param name="transport">Transport to connect over.</param>
    /// <param name="name">Unique name, used for the socket file.</param>
    /// <param name="oneWayDelay">TCP only: delay added in each direction by a proxy between client and server.</param>
    public static async Task<BenchConnection> Start(BenchTransport transport, string name, TimeSpan oneWayDelay = default)
    {
        var connection = new BenchConnection();
        ServerConfig serverConfig;
        ClientConfig clientConfig;

        if (transport == BenchTransport.Uds)
        {
            var path = $"{name}.sock";
            if (File.Exists(path))
                File.Delete(path);
            serverConfig = new UdsServerConfig { EndPoint = new UnixDomainSocketEndPoint(path) };
            clientConfig = new UdsClientConfig { EndPoint = new UnixDomainSocketEndPoint(path) };
        }
        else if (transport == BenchTransport.TcpTls)
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort());
            serverConfig = new TcpTlsServerConfig
            {
                EndPoint = endPoint,
                SslServerAuthenticationOptions = ServerTls(),
            };
            clientConfig = new TcpTlsClientConfig
            {
                EndPoint = endPoint,
                SslClientAuthenticationOptions = ClientTls(),
            };
        }
        else if (transport == BenchTransport.Quic)
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, FreeUdpPort());
            serverConfig = new QuicServerConfig
            {
                EndPoint = endPoint,
                SslServerAuthenticationOptions = ServerTls(),
            };
            clientConfig = new QuicClientConfig
            {
                EndPoint = endPoint,
                SslClientAuthenticationOptions = ClientTls(),
            };
        }
        else
        {
            var serverEndPoint = new IPEndPoint(IPAddress.Loopback, FreeTcpPort());
            var clientEndPoint = serverEndPoint;
            if (oneWayDelay > TimeSpan.Zero)
            {
                connection._proxy = LatencyProxy.Start(serverEndPoint, oneWayDelay);
                clientEndPoint = connection._proxy.EndPoint;
            }

            serverConfig = new TcpServerConfig { EndPoint = serverEndPoint };
            clientConfig = new TcpClientConfig { EndPoint = clientEndPoint };
        }

        connection.Server = ServerNexus.CreateServer(serverConfig, static () => new ServerNexus());
        connection.Client = ClientNexus.CreateClient(clientConfig, new ClientNexus());
        await connection.Server.StartAsync();
        await connection.Client.ConnectAsync();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisconnectAsync();
        await Server.StopAsync();
        _proxy?.Dispose();
    }

    // TLS 1.3 on both sides, the certificate shared with the integration tests (src/server.pfx).
    private static SslServerAuthenticationOptions ServerTls() => new()
    {
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ClientCertificateRequired = false,
        AllowRenegotiation = false,
        EnabledSslProtocols = SslProtocols.Tls13,
        ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile("server.pfx", "certPass"),
    };

    private static SslClientAuthenticationOptions ClientTls() => new()
    {
        // msquic fails the TLS handshake without a DNS server name (SNI).
        TargetHost = "localhost",
        EnabledSslProtocols = SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        AllowRenegotiation = false,
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
    };

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
