using System;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class SocketTransport : ITransport
{
    private readonly SocketConnection _socketConnection;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    private readonly Socket _socket;

    private SocketTransport(SocketConnection socketConnection)
    {
        _socketConnection = socketConnection;
        _socket = socketConnection.Socket;
        Input = socketConnection.Input;
        Output = socketConnection.Output;
    }

    public TransportConfiguration Configurations => new TransportConfiguration();

    public ValueTask CloseAsync(bool linger)
    {
        if (!linger)
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            return ValueTask.CompletedTask;
        }

        _socketConnection.Dispose();
        return ValueTask.CompletedTask;
    }


    public void Dispose()
    {
        CloseAsync(true);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static ValueTask<ITransport> CreateFromSocket(Socket socket, ServerConfig config)
    {
        var pipe = SocketConnection.Create(
            socket,
            config.SendSessionPipeOptions,
            config.ReceiveSessionPipeOptions,
            SocketConnectionOptions.InlineConnect | SocketConnectionOptions.InlineReads | SocketConnectionOptions.InlineWrites);

        return ValueTask.FromResult((ITransport)new SocketTransport(pipe));
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    public static async ValueTask<ITransport> ConnectAsync(ClientConfig clientConfig, EndPoint endPoint,
        SocketType socketType, ProtocolType protocolType, CancellationToken cancellationToken)
    {
        var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);

        SocketConnection.SetRecommendedClientOptions(socket);

        var connectionOptions = SocketConnectionOptions.InlineConnect
                                | SocketConnectionOptions.InlineReads
                                | SocketConnectionOptions.InlineWrites;

        using (var args = new SocketAwaitableEventArgs())
        {
            args.RemoteEndPoint = endPoint;

            try
            {
                using var timeoutCancellation = new CancellationTokenSource();
                await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel);
                // Connection timeout task.
                async Task ConnectionTimeout()
                {
                    try
                    {
                        await Task.Delay(clientConfig.ConnectionTimeout, timeoutCancellation.Token).ConfigureAwait(false);
                        socket.Close(0);
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (clientConfig.ConnectionTimeout >= 0)
                {
                    // ReSharper disable once MethodSupportsCancellation
                    _ = Task.Run(ConnectionTimeout);
                }

                if (!socket.ConnectAsync(args))
                    args.Complete();

                await args;

                timeoutCancellation.Cancel();
            }
            catch (SocketException e)
            {
                throw new TransportException(e.SocketErrorCode, e.Message, e);
            }
            catch (Exception e)
            {
                throw new TransportException(SocketError.ConnectionRefused, e.Message, e);
            }

        }

        var connection = SocketConnection.Create(
            socket,
            clientConfig.SendSessionPipeOptions,
            clientConfig.ReceiveSessionPipeOptions,
            connectionOptions);

        return new SocketTransport(connection);
    }

}
