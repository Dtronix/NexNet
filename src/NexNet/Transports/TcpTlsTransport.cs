using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class TcpTlsTransport : ITransportBase
{
    private readonly NetworkStream _networkStream;
    private readonly SslStream _sslStream;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public Socket Socket { get; }

    private TcpTlsTransport(Socket socket, NetworkStream networkStream, SslStream sslStream)
    {
        _networkStream = networkStream;
        _sslStream = sslStream;
        Socket = socket;
        Input = PipeReader.Create(sslStream);
        Output = PipeWriter.Create(sslStream);
    }


    public void Dispose()
    {
        _sslStream.Dispose();
        _networkStream.Dispose();
        Socket.Dispose();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransportBase> CreateFromSocket(Socket socket, TcpTlsServerConfig config)
    {
        var networkStream = new NetworkStream(socket, false);
        var sslStream = new SslStream(networkStream, true);

        var sslTimeout = new CancellationTokenSource(config.SslConnectionTimeout);

 
        await sslStream.AuthenticateAsServerAsync(config.SslServerAuthenticationOptions, sslTimeout.Token);

        return new TcpTlsTransport(socket, networkStream, sslStream);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransportBase> ConnectAsync(TcpTlsClientConfig clientConfig)
    {
        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);

        var socket = new Socket(clientConfig.SocketAddressFamily, clientConfig.SocketType, clientConfig.SocketProtocolType);

        try
        {
            await socket.ConnectAsync(clientConfig.EndPoint, timeoutCancellation.Token);

            SocketConnection.SetRecommendedClientOptions(socket);

            var networkStream = new NetworkStream(socket, false);
            var sslStream = new SslStream(networkStream, true);

            var sslTimeout = new CancellationTokenSource(clientConfig.SslConnectionTimeout);

            await sslStream.AuthenticateAsClientAsync(clientConfig.SslClientAuthenticationOptions, sslTimeout.Token);

            return new TcpTlsTransport(socket, networkStream, sslStream);
        }
        catch (SocketException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new SocketException((int)SocketError.NotConnected);
        }

    }
}
