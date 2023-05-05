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

    public void Close(bool linger)
    {
        if (!linger)
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close(0);
            return;
        }

        _socketConnection.Dispose();
    }


    public void Dispose()
    {
        Close(true);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static ValueTask<ITransport> CreateFromSocket(Socket socket, ServerConfig config)
    {
        var pipe = SocketConnection.Create(
            socket,
            config.SendPipeOptions,
            config.ReceivePipeOptions,
            SocketConnectionOptions.InlineConnect | SocketConnectionOptions.InlineReads | SocketConnectionOptions.InlineWrites);

        return ValueTask.FromResult((ITransport)new SocketTransport(pipe));
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    public static async ValueTask<ITransport> ConnectAsync(ClientConfig clientConfig, EndPoint endPoint, SocketType socketType, ProtocolType protocolType)
    {
        var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);

        SocketConnection.SetRecommendedClientOptions(socket);

        var connectionOptions = SocketConnectionOptions.InlineConnect
                                | SocketConnectionOptions.InlineReads
                                | SocketConnectionOptions.InlineWrites;

        //using (var args = new SocketAwaitableEventArgs((connectionOptions & SocketConnectionOptions.InlineConnect) == 0 ? PipeScheduler.ThreadPool : null))

        using (var args = new SocketAwaitableEventArgs(null))
        {
            args.RemoteEndPoint = endPoint;

            try
            {
                using var timeoutCancellation = new CancellationTokenSource();
                // Connection timeout task.
                async Task ConnectionTimeout()
                {
                    try
                    {
                        await Task.Delay(clientConfig.ConnectionTimeout, timeoutCancellation.Token);
                        socket.Close(0);
                    }
                    catch (TaskCanceledException e)
                    {
                        return;
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
                throw;
            }
            catch (Exception e)
            {
                throw new SocketException((int)SocketError.NotConnected);
            }

        }

        var connection = SocketConnection.Create(
            socket,
            clientConfig.SendPipeOptions,
            clientConfig.ReceivePipeOptions,
            connectionOptions,
            null);

        return new SocketTransport(connection);
    }

}
