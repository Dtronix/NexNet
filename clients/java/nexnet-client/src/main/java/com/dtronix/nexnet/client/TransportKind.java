package com.dtronix.nexnet.client;

/**
 * The transports of the Java client (D4). Unix domain sockets and QUIC are not available on Java 11 / Android.
 */
public enum TransportKind {
    /** WebSocket ({@code ws://} or {@code wss://}), binary messages, no subprotocol. */
    WEB_SOCKET,
    /** HttpSocket: an HTTP/1.1 upgrade to a raw byte stream ({@code http://} or {@code https://}). */
    HTTP_SOCKET,
    /** Plain TCP. */
    TCP,
    /** TCP with TLS. */
    TCP_TLS
}
