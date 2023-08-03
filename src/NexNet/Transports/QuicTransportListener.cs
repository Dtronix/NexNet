using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;
#pragma warning disable CA1416

namespace NexNet.Transports;

internal class QuicTransportListener : ITransportListener
{
    private readonly QuicServerConfig _config;
    private readonly QuicListener _listener;

    private QuicTransportListener(QuicServerConfig config, QuicListener listener)
    {
        _config = config;
        _listener = listener;
    }

    public ValueTask Close(bool linger)
    {
        return _listener.DisposeAsync();
    }

    public async Task<ITransport?> AcceptTransportAsync()
    {

        var connection = await _listener.AcceptConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        
        try
        {
            return await QuicTransport.CreateFromConnection(connection, _config).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _config.Logger?.LogError(e, "Client attempted to connect but failed with exception.");

            // Immediate disconnect.
            await connection.DisposeAsync();
        }

        return null;
    }

    public static async ValueTask<ITransportListener> Create(QuicServerConfig config, CancellationToken cancellationToken)
    {

        config.SslServerAuthenticationOptions.ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("nn1"),
        };

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

            // Same options as for server side SslStream.
            ServerAuthenticationOptions = config.SslServerAuthenticationOptions
        };

        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            // Listening endpoint, port 0 means any port.
            ListenEndPoint = config.EndPoint,
            // List of all supported application protocols by this listener.
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new SslApplicationProtocol("nn1"),
            },

            // Callback to provide options for the incoming connections, it gets called once per each connection.
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions),
            ListenBacklog = config.AcceptorBacklog,
        }, cancellationToken);

        return new QuicTransportListener(config, listener);
    }
}
