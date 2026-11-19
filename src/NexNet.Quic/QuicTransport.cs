using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals.Pipelines;
using NexNet.Transports;

#pragma warning disable CA1416

namespace NexNet.Quic;

internal class QuicTransport : ITransport
{

    private readonly QuicConnection _quicConnection;

    private readonly QuicStream _quicStream;
    private readonly StreamSendPipe _sendPipe;

    public PipeReader Input { get; }
    public PipeWriter Output => _sendPipe.Writer;
    public string? RemoteAddress { get; }
    public int? RemotePort { get; }
    public X509Certificate2? RemoteCertificate => _quicConnection.RemoteCertificate as X509Certificate2;

    private QuicTransport(QuicConnection quicConnection, QuicStream stream, PipeOptions sendPipeOptions)
    {
        _quicConnection = quicConnection;
        _quicStream = stream;
        // Leave the stream open: the session completes the input when its read loop exits, which can happen before
        // the send loop has written the last frames (the disconnect message). Disposing the stream there would end
        // the stream (FIN) ahead of that data, and the peer would see a bare close. CloseAsync ends the stream.
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _sendPipe = new StreamSendPipe(stream, sendPipeOptions);

        (RemoteAddress, RemotePort) = GetRemoteEndPointInfo(quicConnection);
    }

    /// <summary>
    /// The remote address and port of a QUIC connection.
    /// </summary>
    internal static (string? Address, int? Port) GetRemoteEndPointInfo(QuicConnection connection)
    {
        return connection.RemoteEndPoint is System.Net.IPEndPoint ipEndPoint
            ? (ipEndPoint.Address.ToString(), ipEndPoint.Port)
            : (connection.RemoteEndPoint?.ToString(), null);
    }

    public async ValueTask CloseAsync(bool linger)
    {
        if (linger)
        {
            // Let the send loop write what the session flushed before it completed the output.
            await _sendPipe.DrainAsync(StreamSendPipe.DrainTimeout).ConfigureAwait(false);

            // A written frame may still be in MsQuic's send buffer, and closing the connection discards stream data
            // the peer has not received: a disconnect message lost that way reaches the peer as a bare close
            // (SocketError). End the stream after that data (FIN) and wait for the peer to acknowledge it.
            await CompleteWritesAsync(StreamSendPipe.DrainTimeout).ConfigureAwait(false);
        }
        else
        {
            _sendPipe.Abort();
        }

        await _quicConnection.CloseAsync(0).ConfigureAwait(false);
        await _quicStream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the write side of the stream gracefully and waits up to <paramref name="timeout"/> for the peer to
    /// acknowledge everything written, FIN included. Best effort: a stream already aborted or closed is left as is.
    /// </summary>
    private async ValueTask CompleteWritesAsync(TimeSpan timeout)
    {
        try
        {
            _quicStream.CompleteWrites();
        }
        catch (Exception e) when (e is QuicException or ObjectDisposedException or InvalidOperationException)
        {
            return;
        }

        var writesClosed = _quicStream.WritesClosed;
        if (!writesClosed.IsCompleted)
        {
            using var delayCancellation = new CancellationTokenSource();
            var finished = await Task.WhenAny(writesClosed, Task.Delay(timeout, delayCancellation.Token))
                .ConfigureAwait(false);
            if (finished == writesClosed)
                await delayCancellation.CancelAsync().ConfigureAwait(false);
        }

        // Observe a failure (the peer aborted its reads, or the connection closed) so it is not unobserved.
        if (writesClosed.IsFaulted)
            _ = writesClosed.Exception;
    }

    public void Dispose()
    {
        _quicStream.DisposeAsync();
        _quicConnection.DisposeAsync();
    }

    /// <summary>
    /// Accepts the NexNet stream of an accepted server connection. Bounded by
    /// <see cref="ConfigBase.HandshakeTimeout"/> and by <paramref name="cancellationToken"/>: a peer that never opens
    /// a stream fails here instead of waiting forever. The caller disposes the connection on failure.
    /// </summary>
    public static async ValueTask<ITransport> CreateFromConnection(QuicConnection connection, QuicServerConfig config,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.HandshakeTimeout);
        var stream = await connection.AcceptInboundStreamAsync(timeout.Token).ConfigureAwait(false);
        return new QuicTransport(connection, stream, config.SendSessionPipeOptions);
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    /// 
    public static async ValueTask<ITransport> ConnectAsync(QuicClientConfig clientConfig,
        CancellationToken cancellationToken)
    {
        if (QuicConnection.IsSupported == false)

            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        var connectionOptions = new QuicClientConnectionOptions()
        {
            RemoteEndPoint = clientConfig.EndPoint,
            // Set the application protocol on a copy, leaving the caller's options unchanged.
            ClientAuthenticationOptions = QuicHelpers.WithNexNetAlpn(clientConfig.SslClientAuthenticationOptions),
            DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
            DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.
        };

        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);
        await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel).ConfigureAwait(false);

        QuicConnection quicConnection;
        try
        {
            quicConnection = await QuicConnection.ConnectAsync(connectionOptions, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException e)
        {
            // The server certificate was rejected (chain, host name or validation callback), or the server
            // rejected the client certificate.
            throw new TransportException(TransportError.AuthenticationError, $"TLS authentication failed: {e.Message}", e);
        }
        catch (QuicException e)
        {
            throw new TransportException(QuicHelpers.GetTransportError(e.QuicError), e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, e.Message, e);
        }

        QuicStream mainStream;
        try
        {
            mainStream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The connection is established but unusable without its stream: close it so it does not linger
            // until the peer's idle timeout.
            await quicConnection.DisposeAsync().ConfigureAwait(false);

            // Report it like any other connect failure.
            throw e switch
            {
                OperationCanceledException =>
                    new TransportException(TransportError.ConnectionTimeout, "Opening the QUIC stream timed out.", e),
                QuicException quicException =>
                    new TransportException(QuicHelpers.GetTransportError(quicException.QuicError), e.Message, e),
                _ => new TransportException(TransportError.ConnectionRefused, e.Message, e),
            };
        }

        return new QuicTransport(quicConnection, mainStream, clientConfig.SendSessionPipeOptions);
    }

}
