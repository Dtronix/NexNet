using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class TcpTlsTransport : ITransport
{
    private readonly NetworkStream _networkStream;
    private readonly SslStream _sslStream;
    private readonly Socket _socket;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    private TcpTlsTransport(Socket socket, NetworkStream networkStream, SslStream sslStream)
    {
        _networkStream = networkStream;
        _sslStream = sslStream;
        _socket = socket;
        Input = PipeReader.Create(sslStream);
        Output = PipeWriter.Create(sslStream);
    }

    public ValueTask CloseAsync(bool linger)
    {
        if (!linger)
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            return ValueTask.CompletedTask;
        }

        _sslStream.Dispose();
        _networkStream.Dispose();
        _socket.Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> CreateFromSocket(Socket socket, TcpTlsServerConfig config)
    {
        var networkStream = new NetworkStream(socket, false);
        var sslStream = new SslStream(networkStream, true);

        var sslTimeout = new CancellationTokenSource(config.SslConnectionTimeout);

 
        await sslStream.AuthenticateAsServerAsync(config.SslServerAuthenticationOptions, sslTimeout.Token)
            .ConfigureAwait(false);

        return new TcpTlsTransport(socket, networkStream, sslStream);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> ConnectAsync(TcpTlsClientConfig clientConfig, EndPoint endPoint, SocketType socketType, ProtocolType protocolType)
    {
        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);

        var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);

        try
        {
            await socket.ConnectAsync(clientConfig.EndPoint, timeoutCancellation.Token).ConfigureAwait(false);

            SocketConnection.SetRecommendedClientOptions(socket);

            var networkStream = new NetworkStream(socket, false);
            var sslStream = new SslStream(networkStream, true);

            var sslTimeout = new CancellationTokenSource(clientConfig.SslConnectionTimeout);

            await sslStream.AuthenticateAsClientAsync(clientConfig.SslClientAuthenticationOptions, sslTimeout.Token)
                .ConfigureAwait(false);

            return new TcpTlsTransport(socket, networkStream, sslStream);
        }
        catch (SocketException e)
        {
            throw new TransportException(e.SocketErrorCode, e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, "Connection failed to connect.", e);
        }

    }
}
