using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NexNet;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNetBenchmarks;

public enum BenchTransport
{
    Uds,
    Tcp,
}

/// <summary>
/// A connected client and server over UDS or loopback TCP, optionally with the client connecting through a
/// <see cref="LatencyProxy"/>.
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

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
