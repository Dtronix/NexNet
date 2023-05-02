using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;

namespace NexNet.Transports;

internal class SocketTransport : ITransportBase
{
    private readonly SocketConnection _socketConnection;
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    public Socket Socket { get; }

    private SocketTransport(SocketConnection socketConnection)
    {
        _socketConnection = socketConnection;
        Socket = socketConnection.Socket;
        Input = socketConnection.Input;
        Output = socketConnection.Output;
    }


    public void Dispose()
    {
        _socketConnection.Dispose();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static ValueTask<ITransportBase> CreateFromSocket(Socket socket, ServerConfig config)
    {
        var pipe = SocketConnection.Create(
            socket,
            config.SendPipeOptions,
            config.ReceivePipeOptions,
            SocketConnectionOptions.InlineConnect | SocketConnectionOptions.InlineReads | SocketConnectionOptions.InlineWrites);

        return ValueTask.FromResult((ITransportBase)new SocketTransport(pipe));
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransportBase> ConnectAsync(ClientConfig clientConfig)
    {
        var socket = new Socket(clientConfig.SocketAddressFamily, clientConfig.SocketType, clientConfig.SocketProtocolType);

        SocketConnection.SetRecommendedClientOptions(socket);

        var connectionOptions = SocketConnectionOptions.InlineConnect
                                | SocketConnectionOptions.InlineReads
                                | SocketConnectionOptions.InlineWrites;

        //using (var args = new SocketAwaitableEventArgs((connectionOptions & SocketConnectionOptions.InlineConnect) == 0 ? PipeScheduler.ThreadPool : null))

        using (var args = new SocketAwaitableEventArgs(null))
        {
            args.RemoteEndPoint = clientConfig.SocketEndPoint;

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
