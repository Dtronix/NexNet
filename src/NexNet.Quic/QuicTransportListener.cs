using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Transports;

#pragma warning disable CA1416

namespace NexNet.Quic;

internal class QuicTransportListener : IPendingTransportListener, IBoundTransportListener
{
    private readonly QuicServerConfig _config;
    private readonly QuicListener _listener;

    private QuicTransportListener(QuicServerConfig config, QuicListener listener)
    {
        _config = config;
        _listener = listener;
        // The UDP end point msquic bound; a configured port 0 resolves to the picked port here.
        BoundEndPoint = listener.LocalEndPoint;
    }

    /// <inheritdoc />
    public EndPoint? BoundEndPoint { get; }

    public ValueTask CloseAsync(bool linger)
    {
        return _listener.DisposeAsync();
    }

    /// <summary>
    /// Accepts the next connection and its NexNet stream inline. Returns null when the accept was canceled, when a
    /// client's handshake failed, or when an accepted connection could not be set up. Only a disposed listener
    /// (<see cref="ObjectDisposedException"/>) is rethrown so the server faults the endpoint.
    /// </summary>
    /// <remarks>
    /// The server does not use this: it calls <see cref="AcceptPendingAsync"/> and accepts each connection's stream
    /// on its own task, so a peer that never opens a stream does not block other accepts.
    /// </remarks>
    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        var pending = await AcceptPendingAsync(cancellationToken).ConfigureAwait(false);
        return pending == null
            ? null
            : await pending.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the next connection; its NexNet stream is accepted later in
    /// <see cref="IPendingTransport.CompleteAsync"/>. QUIC performs the TLS handshake inside
    /// <see cref="QuicListener.AcceptConnectionAsync"/> (handshakes run concurrently in the listener), so a single
    /// client's failed handshake (rejected certificate, ALPN mismatch, handshake timeout) surfaces there. Such
    /// per-connection failures are transient: they are logged and null is returned so the listener keeps accepting.
    /// Returns null as well when the accept was canceled. Only a disposed listener
    /// (<see cref="ObjectDisposedException"/>) is rethrown so the server faults the endpoint.
    /// </summary>
    public async ValueTask<IPendingTransport?> AcceptPendingAsync(CancellationToken cancellationToken)
    {
        QuicConnection connection;
        try
        {
            connection = await _listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is not ObjectDisposedException)
        {
            _config.Logger?.LogWarning(e, "Client connection failed during the QUIC handshake; listener continues.");
            return null;
        }

        return new PendingQuicTransport(connection, _config);
    }

    /// <summary>
    /// A QUIC connection whose NexNet stream has not been accepted yet.
    /// </summary>
    private sealed class PendingQuicTransport : IPendingTransport
    {
        private readonly QuicConnection _connection;
        private readonly QuicServerConfig _config;

        public string? RemoteAddress { get; }

        public PendingQuicTransport(QuicConnection connection, QuicServerConfig config)
        {
            _connection = connection;
            _config = config;
            RemoteAddress = QuicTransport.GetRemoteEndPointInfo(connection).Address;
        }

        public async ValueTask<ITransport?> CompleteAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await QuicTransport.CreateFromConnection(_connection, _config, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");
                await DisposeQuietlyAsync().ConfigureAwait(false);
                return null;
            }
        }

        public void Abort()
        {
            _ = DisposeQuietlyAsync();
        }

        private async Task DisposeQuietlyAsync()
        {
            // Immediate disconnect.
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                _config.Logger?.LogDebug(disposeException, "Failed to dispose the rejected client connection.");
            }
        }
    }

    public static async ValueTask<ITransportListener> Create(QuicServerConfig config, CancellationToken cancellationToken)
    {
        // Share configuration for each incoming connection.
        // This represents the minimal configuration necessary.
        var serverConnectionOptions = new QuicServerConnectionOptions
        {
            // Used to abort stream if it's not properly closed by the user.
            // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
            DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.

            // Used to close the connection if it's not done by the user.
            // See https://www.rfc-editor.org/rfc/rfc9000#section-20.2
            DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.

            // Same options as for server side SslStream, on a copy so the ALPN does not leak into the caller's
            // object (which may be shared with a TLS listener).
            ServerAuthenticationOptions = QuicHelpers.WithNexNetAlpn(config.SslServerAuthenticationOptions)
        };

        try
        {
            var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                // Listening endpoint, port 0 means any port.
                ListenEndPoint = config.EndPoint,
                // List of all supported application protocols by this listener.
                ApplicationProtocols = new List<SslApplicationProtocol> { QuicHelpers.Alpn },

                // Callback to provide options for the incoming connections, it gets called once per each connection.
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions),
                ListenBacklog = config.AcceptorBacklog
            }, cancellationToken).ConfigureAwait(false);

            return new QuicTransportListener(config, listener);
        }
        catch (QuicException e)
        {
            throw new TransportException(QuicHelpers.GetTransportError(e.QuicError), e.Message, e);
        }
        catch (Exception)
        {
            throw;
        }

    }
}
