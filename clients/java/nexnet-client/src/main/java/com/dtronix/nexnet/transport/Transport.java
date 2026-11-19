package com.dtronix.nexnet.transport;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ClientConfig;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.security.cert.X509Certificate;
import org.jspecify.annotations.Nullable;

/**
 * A connected byte stream that carries one NexNet session: TCP, TLS, HttpSocket or WebSocket. NexNet frames may span
 * any number of reads and writes; a transport never aligns them with its own units.
 *
 * <p>Threading: the session reads {@link #input()} only from its reader thread and writes {@link #output()} only
 * from its writer thread, so the streams need not be thread-safe. {@link #close(DisconnectReason)} may be called from
 * any thread, at any time and more than once; it must unblock a read or write in progress (which then throws or
 * returns end of stream).
 *
 * <p>Bytes the server sent together with an upgrade response (HTTP {@code 101}) belong to the NexNet session: the
 * transport returns them first from {@link #input()}.
 */
public interface Transport {
    /**
     * The stream of bytes from the server. Blocking reads; end of stream when the connection closes.
     *
     * @return the input stream
     */
    InputStream input();

    /**
     * The stream of bytes to the server. Blocking writes; {@link OutputStream#flush()} pushes buffered bytes to the
     * network.
     *
     * @return the output stream
     */
    OutputStream output();

    /**
     * Whether the transport encrypts the connection (D7). A transport that does not declare itself secure is not
     * (fail safe): only TLS, {@code wss://} and {@code https://} transports return true.
     *
     * @return true for an encrypted transport
     */
    default boolean isSecure() {
        return false;
    }

    /**
     * The server certificate of a TLS connection.
     *
     * @return the leaf certificate, or null for a plaintext connection
     */
    default @Nullable X509Certificate remoteCertificate() {
        return null;
    }

    /**
     * The transport name used in logs and the plaintext warning, for example {@code tcp} or {@code ws://}.
     *
     * @return the name
     */
    String name();

    /**
     * The remote address.
     *
     * @return the address, or null when unknown
     */
    default @Nullable String remoteAddress() {
        return null;
    }

    /**
     * The remote port.
     *
     * @return the port, or -1 when unknown
     */
    default int remotePort() {
        return -1;
    }

    /**
     * Closes the connection. Idempotent, never throws, and unblocks a read or write in progress. The session has
     * already written the disconnect byte when one is due; the reason is informational (for example a WebSocket close
     * code).
     *
     * @param reason why the session ended
     */
    void close(DisconnectReason reason);

    /**
     * Opens a transport for one connection attempt. Called on a runtime thread, never on the caller of
     * {@code connect()}.
     */
    @FunctionalInterface
    interface Connector {
        /**
         * Connects. The transport is ready for the NexNet preamble when this returns.
         *
         * @param config the client configuration (transport kind, address, TLS options)
         * @param timeoutMillis the time left for the connection attempt
         * @return the connected transport
         * @throws IOException when the connection fails; a {@code NexusTransportException} carries the
         *     {@code TransportError}
         */
        Transport connect(ClientConfig config, int timeoutMillis) throws IOException;
    }
}
