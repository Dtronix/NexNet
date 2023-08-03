using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial;
#pragma warning disable CA1416

namespace NexNet.Transports;

/// <summary>
/// Lists the possible errors that can occur during transport operations.
/// </summary>
public enum TransportError
{
    /// <summary>
    /// No error.
    /// </summary>
    Success,

    /// <summary>
    /// An internal implementation error has occurred.
    /// </summary>
    InternalError,

    /// <summary>
    /// The connection was aborted by the peer. This error is associated with an application-level error code.
    /// </summary>
    ConnectionAborted,

    /// <summary>
    /// The read or write direction of the stream was aborted by the peer. This error is associated with an application-level error code.
    /// </summary>
    StreamAborted,

    /// <summary>
    /// The local address is already in use.
    /// </summary>
    AddressInUse,

    /// <summary>
    /// Binding to socket failed, likely caused by a family mismatch between local and remote address.
    /// </summary>
    InvalidAddress,

    /// <summary>
    /// The connection timed out waiting for a response from the peer.
    /// </summary>
    ConnectionTimeout,

    /// <summary>
    /// The server is currently unreachable.
    /// </summary>
    Unreachable,

    /// <summary>
    /// The server refused the connection.
    /// </summary>
    ConnectionRefused,

    /// <summary>
    /// A version negotiation error was encountered.
    /// </summary>
    VersionNegotiationError,

    /// <summary>
    /// The connection timed out from inactivity.
    /// </summary>
    ConnectionIdle,

    /// <summary>
    /// A QUIC protocol error was encountered.
    /// </summary>
    ProtocolError,

    /// <summary>
    /// The operation has been aborted.
    /// </summary>
    OperationAborted,

    /// <summary>
    /// The connection was reset by the peer.
    /// </summary>
    ConnectionReset,
}

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
    public ValueTask Close(bool linger)
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
    public static async ValueTask<ITransport> ConnectAsync(QuicClientConfig clientConfig)
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

        var mainStream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeoutCancellation.Token);

        return new QuicTransport(quicConnection, mainStream);
    }

}
