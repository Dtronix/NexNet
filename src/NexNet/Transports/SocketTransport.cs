using System;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Pipelines;

namespace NexNet.Transports;

/// <summary>
/// TCP and Unix domain socket transport. A receive loop reads the socket into a <see cref="Pipe"/>
/// (<see cref="ConfigBase.ReceiveSessionPipeOptions"/>: read-ahead with back pressure); output goes through a
/// <see cref="StreamSendPipe"/> over a <see cref="NetworkStream"/>, the send model of the stream transports.
/// </summary>
/// <remarks>
/// <see cref="Socket.ReceiveAsync(Memory{byte}, SocketFlags, CancellationToken)"/> and the stream's writes complete
/// through the socket's cached awaitable event args, so neither direction allocates per operation.
/// </remarks>
internal sealed class SocketTransport : ITransport
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly Pipe _receivePipe;
    private readonly StreamSendPipe _sendPipe;
    private readonly CancellationTokenSource _receiveAbort = new();
    private readonly Task _receiveLoop;
    private int _closed;

    public PipeReader Input => _receivePipe.Reader;
    public PipeWriter Output => _sendPipe.Writer;
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate => null;

    private SocketTransport(Socket socket, PipeOptions sendPipeOptions, PipeOptions receivePipeOptions)
    {
        _socket = socket;
        (RemoteAddress, RemotePort) = EndPointHelper.ExtractEndPointInfo(socket.RemoteEndPoint);
        _stream = new NetworkStream(socket, ownsSocket: false);
        _receivePipe = new Pipe(receivePipeOptions);
        _sendPipe = new StreamSendPipe(_stream, sendPipeOptions);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public async ValueTask CloseAsync(bool linger)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        if (!linger)
        {
            // Abortive close: reset the connection, drop what is buffered.
            _sendPipe.Abort();
            await _receiveAbort.CancelAsync().ConfigureAwait(false);
            try
            {
                _socket.LingerState = new LingerOption(true, 0);
                _socket.Close(0);
            }
            catch (ObjectDisposedException)
            {
                // Already closed.
            }

            return;
        }

        // Let the send loop write what the session flushed before it completed the output, then end both
        // directions (FIN) and close.
        await _sendPipe.DrainAsync(StreamSendPipe.DrainTimeout).ConfigureAwait(false);
        await _receiveAbort.CancelAsync().ConfigureAwait(false);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // The peer is already gone.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket.Close();
    }

    private async Task ReceiveLoopAsync()
    {
        var writer = _receivePipe.Writer;
        var cancellationToken = _receiveAbort.Token;
        Exception? error = null;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(1);
                var received = await _socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);

                // The peer ended its side of the connection.
                if (received == 0)
                    break;

                writer.Advance(received);
                var flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                    break;
            }
        }
        catch (Exception e) when (cancellationToken.IsCancellationRequested
                                  && e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Closed by this side: the session is already disconnecting.
        }
        catch (SocketException e)
        {
            // Reset, aborted or failed: the session's read loop sees the error and disconnects (SocketError).
            error = new IOException(e.Message, e);
        }
        catch (ObjectDisposedException e)
        {
            error = new IOException("The connection was closed.", e);
        }
        catch (Exception e)
        {
            error = e as IOException ?? new IOException(e.Message, e);
        }
        finally
        {
            // Pending data stays readable; the reader sees the end (or the error) after it.
            await writer.CompleteAsync(error).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disables Nagle on TCP sockets (no effect on Unix domain sockets): frames are flushed as they are written.
    /// </summary>
    internal static void SetNoDelay(Socket socket)
    {
        if (socket.AddressFamily == AddressFamily.Unix)
            return;

        try
        {
            socket.NoDelay = true;
        }
        catch (SocketException)
        {
            // Not supported by this socket; frames are still sent.
        }
    }

    /// <summary>
    /// Open a new or existing socket as a server accepting a client connection
    /// </summary>
    public static ValueTask<ITransport> CreateFromSocket(Socket socket, ServerConfig config)
        => ValueTask.FromResult<ITransport>(
            new SocketTransport(socket, config.SendSessionPipeOptions, config.ReceiveSessionPipeOptions));

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> ConnectAsync(ClientConfig clientConfig, EndPoint endPoint,
        SocketType socketType, ProtocolType protocolType, CancellationToken cancellationToken)
    {
        var socket = new Socket(endPoint.AddressFamily, socketType, protocolType);
        SetNoDelay(socket);

        // Real TCP/Uds connect happens on the OS schedule; this timeout governs the real-network handshake, not
        // application logic, so it stays on real time.
        using var timeout = clientConfig.ConnectionTimeout >= 0
            ? new CancellationTokenSource(clientConfig.ConnectionTimeout)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await socket.ConnectAsync(endPoint, linked.Token).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new TransportException(e.SocketErrorCode, e.Message, e);
        }
        catch (OperationCanceledException e) when (timeout.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TransportException(SocketError.TimedOut, "Connection timed out.", e);
        }
        catch (Exception e)
        {
            socket.Dispose();
            throw new TransportException(SocketError.ConnectionRefused, e.Message, e);
        }

        return new SocketTransport(socket, clientConfig.SendSessionPipeOptions, clientConfig.ReceiveSessionPipeOptions);
    }
}
