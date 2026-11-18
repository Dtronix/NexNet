package com.dtronix.nexnet.client;

/**
 * The state of a client connection.
 */
public enum ConnectionState {
    /** Not set. */
    UNSET,
    /** Connecting: transport and handshake in progress. */
    CONNECTING,
    /** Connected and ready. */
    CONNECTED,
    /** The connection was lost; the client is reconnecting. */
    RECONNECTING,
    /** Closing. */
    DISCONNECTING,
    /** Closed. */
    DISCONNECTED
}
