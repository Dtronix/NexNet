using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Pipelines;

namespace NexNet.Transports;

internal class TcpTlsTransport : ITransport
{
    private readonly NetworkStream _networkStream;
    private readonly SslStream _sslStream;
    private readonly Socket _socket;
    private readonly StreamSendPipe _sendPipe;
    public PipeReader Input { get; }
    public PipeWriter Output => _sendPipe.Writer;
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate => _sslStream.RemoteCertificate as X509Certificate2;

    private TcpTlsTransport(Socket socket, NetworkStream networkStream, SslStream sslStream,
        PipeOptions sendPipeOptions, string? remoteAddress, int? remotePort)
    {
        _networkStream = networkStream;
        _sslStream = sslStream;
        _socket = socket;
        Input = PipeReader.Create(sslStream);
        _sendPipe = new StreamSendPipe(sslStream, sendPipeOptions);
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
    }

    public async ValueTask CloseAsync(bool linger)
    {
        if (!linger)
        {
            _sendPipe.Abort();
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            return;
        }

        // Let the send loop write what the session flushed before it completed the output.
        await _sendPipe.DrainAsync(StreamSendPipe.DrainTimeout).ConfigureAwait(false);
        await _sslStream.DisposeAsync().ConfigureAwait(false);
        await _networkStream.DisposeAsync().ConfigureAwait(false);
        _socket.Close();
    }

    /// <summary>
    /// Open a new or existing socket as a server accepting a client
    /// </summary>
    public static async ValueTask<ITransport> CreateFromSocket(Socket socket, TcpTlsServerConfig config)
    {
        var networkStream = new NetworkStream(socket, false);
        var sslStream = new SslStream(networkStream, true);

        var sslTimeout = new CancellationTokenSource(config.SslConnectionTimeout);


        await sslStream.AuthenticateAsServerAsync(config.SslServerAuthenticationOptions, sslTimeout.Token)
            .ConfigureAwait(false);

        // Extract remote endpoint
        var (remoteAddress, remotePort) = EndPointHelper.ExtractEndPointInfo(socket.RemoteEndPoint);

        return new TcpTlsTransport(socket, networkStream, sslStream, config.SendSessionPipeOptions, remoteAddress, remotePort);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> ConnectAsync(TcpTlsClientConfig clientConfig, EndPoint endPoint,
        SocketType socketType, ProtocolType protocolType, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);

        var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);

        try
        {
            await socket.ConnectAsync(clientConfig.EndPoint, timeoutCancellation.Token).ConfigureAwait(false);

            SocketConnection.SetRecommendedClientOptions(socket);

            var networkStream = new NetworkStream(socket, false);
            var sslStream = new SslStream(networkStream, true);

            using var sslTimeout = new CancellationTokenSource(clientConfig.SslConnectionTimeout);
            await using var cancellationTokenRegistration = cancellationToken.Register(sslTimeout.Cancel)
                .ConfigureAwait(false);

            await sslStream.AuthenticateAsClientAsync(clientConfig.SslClientAuthenticationOptions, sslTimeout.Token)
                .ConfigureAwait(false);

            var (remoteAddress, remotePort) = EndPointHelper.ExtractEndPointInfo(socket.RemoteEndPoint);

            return new TcpTlsTransport(socket, networkStream, sslStream, clientConfig.SendSessionPipeOptions, remoteAddress, remotePort);
        }
        catch (SocketException e)
        {
            throw new TransportException(e.SocketErrorCode, e.Message, e);
        }
        catch (AuthenticationException e)
        {
            throw new TransportException(TransportError.AuthenticationError, $"TLS authentication failed: {e.Message}", e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, "Connection failed to connect.", e);
        }

    }
}
