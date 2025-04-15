using System;
using System.Net.Sockets;

namespace NexNet.Transports;

/// <summary>
/// Exception for transport errors. Used to abstract away the underlying transport.
/// </summary>
public class TransportException : Exception
{
    /// <summary>
    /// Error code
    /// </summary>
    public TransportError Error { get; }

    /// <summary>
    /// Exception for transport errors. Used to abstract away the underlying transport.
    /// </summary>
    /// <param name="error">Error code.</param>
    /// <param name="message">Error description.</param>
    /// <param name="innerException">Source exception.</param>
    public TransportException(TransportError error, string message, Exception? innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Exception for transport errors. Used to abstract away the underlying transport.
    /// </summary>
    /// <param name="error">Error code.</param>
    /// <param name="message">Error description.</param>
    /// <param name="innerException">Source exception.</param>
    public TransportException(SocketError error, string message, Exception? innerException)
        : base(message, innerException)
    {
        Error = SocketErrorToTransportError(error);
    }

    private static TransportError SocketErrorToTransportError(SocketError error) =>
        error switch
        {
            SocketError.ConnectionRefused => TransportError.ConnectionRefused,
            SocketError.ConnectionReset => TransportError.ConnectionReset,
            SocketError.ConnectionAborted => TransportError.ConnectionAborted,
            SocketError.HostUnreachable => TransportError.Unreachable,
            SocketError.HostNotFound => TransportError.Unreachable,
            SocketError.TimedOut => TransportError.ConnectionTimeout,
            SocketError.NetworkUnreachable => TransportError.Unreachable,
            SocketError.NetworkDown => TransportError.Unreachable,
            SocketError.NetworkReset => TransportError.Unreachable,
            SocketError.Shutdown => TransportError.OperationAborted,
            SocketError.NotConnected => TransportError.OperationAborted,
            SocketError.AddressNotAvailable => TransportError.Unreachable,
            SocketError.AddressAlreadyInUse => TransportError.AddressInUse,
            SocketError.AccessDenied => TransportError.Unreachable,
            SocketError.MessageSize => TransportError.ProtocolError,
            SocketError.ProtocolNotSupported => TransportError.ProtocolError,
            SocketError.ProtocolOption => TransportError.ProtocolError,
            SocketError.ProtocolType => TransportError.ProtocolError,
            SocketError.SocketNotSupported => TransportError.ProtocolError,
            _ => TransportError.InternalError
        };
}
