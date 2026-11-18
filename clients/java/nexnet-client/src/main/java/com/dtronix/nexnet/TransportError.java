package com.dtronix.nexnet;

/**
 * The transport failures a {@link NexusTransportException} reports. Mirrors the C# {@code TransportError}.
 */
public enum TransportError {
    /** An internal implementation error. */
    INTERNAL_ERROR,
    /** The peer aborted the connection. */
    CONNECTION_ABORTED,
    /** The address is invalid or of the wrong family. */
    INVALID_ADDRESS,
    /** The connection timed out waiting for the peer. */
    CONNECTION_TIMEOUT,
    /** The server is unreachable. */
    UNREACHABLE,
    /** The server refused the connection. */
    CONNECTION_REFUSED,
    /** The connection timed out from inactivity. */
    CONNECTION_IDLE,
    /** The transport protocol (HTTP upgrade, WebSocket framing, TLS record) was violated. */
    PROTOCOL_ERROR,
    /** The operation was aborted. */
    OPERATION_ABORTED,
    /** The peer reset the connection. */
    CONNECTION_RESET,
    /** TLS authentication failed, or the authenticate supplier failed or timed out. */
    AUTHENTICATION_ERROR
}
