﻿namespace NexNet.Transports;

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
    
    /// <summary>
    /// The connection authentication failed
    /// </summary>
    AuthenticationError,
}
