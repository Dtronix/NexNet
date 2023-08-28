using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

#pragma warning disable CA1416

namespace NexNet.Quic;

internal class QuicTransport : ITransport
{

    private readonly QuicConnection _quicConnection;
 
    private readonly QuicStream _quicStream;

    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    private QuicTransport(QuicConnection quicConnection, QuicStream stream)
    {
        _quicConnection = quicConnection;
        _quicStream = stream;
        Input = PipeReader.Create(stream);
        Output = PipeWriter.Create(stream);
    }

    public TransportConfiguration Configurations => new TransportConfiguration()
    {
        DoNotPassFlushCancellationToken = true
    };

    public ValueTask CloseAsync(bool linger)
    {
        return _quicConnection.CloseAsync(0);
    }

    public void Dispose()
    {
        _quicConnection.DisposeAsync();
    }

    /// <summary>
    /// Open a new or existing socket as a client
    /// </summary>
    public static async ValueTask<ITransport> CreateFromConnection(QuicConnection connection, QuicServerConfig config)
    {
        var stream = await connection.AcceptInboundStreamAsync(CancellationToken.None);
        return new QuicTransport(connection, stream);
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

        // Set the application protocol.
        clientConfig.SslClientAuthenticationOptions.ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("nn1"),
        };
        var connectionOptions = new QuicClientConnectionOptions()
        {
            RemoteEndPoint = clientConfig.EndPoint,
            ClientAuthenticationOptions = clientConfig.SslClientAuthenticationOptions,
            DefaultStreamErrorCode = 0x0A, // Protocol-dependent error code.
            DefaultCloseErrorCode = 0x0B, // Protocol-dependent error code.
        };

        using var timeoutCancellation = new CancellationTokenSource(clientConfig.ConnectionTimeout);
        await using var cancellationTokenRegistration = cancellationToken.Register(timeoutCancellation.Cancel);

        QuicConnection quicConnection;
        try
        {
            quicConnection = await QuicConnection.ConnectAsync(connectionOptions, timeoutCancellation.Token);
        }
        catch (QuicException e)
        {
            var error = e.QuicError switch
            {
                QuicError.InternalError => TransportError.InternalError,
                QuicError.ConnectionAborted => TransportError.ConnectionAborted,
                QuicError.StreamAborted => TransportError.StreamAborted,
                QuicError.AddressInUse => TransportError.AddressInUse,
                QuicError.InvalidAddress => TransportError.InvalidAddress,
                QuicError.ConnectionTimeout => TransportError.ConnectionTimeout,
                QuicError.HostUnreachable => TransportError.Unreachable,
                QuicError.ConnectionRefused => TransportError.ConnectionRefused,
                QuicError.VersionNegotiationError => TransportError.VersionNegotiationError,
                QuicError.ConnectionIdle => TransportError.ConnectionIdle,
                QuicError.ProtocolError => TransportError.ProtocolError,
                QuicError.OperationAborted => TransportError.OperationAborted,
                _ => TransportError.InternalError,
            };

            throw new TransportException(error, e.Message, e);
        }
        catch (Exception e)
        {
            throw new TransportException(TransportError.ConnectionRefused, e.Message, e);
        }

        var mainStream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeoutCancellation.Token);

        return new QuicTransport(quicConnection, mainStream);
    }

}
