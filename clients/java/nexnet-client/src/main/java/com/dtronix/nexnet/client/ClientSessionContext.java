package com.dtronix.nexnet.client;

import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import java.security.cert.X509Certificate;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * The session as seen from a client nexus ({@code ClientNexusBase.context()}), like the C#
 * {@code ClientSessionContext<TProxy>}. Implemented by the session runtime (K05).
 *
 * @param <TProxy> the generated server proxy type
 */
public interface ClientSessionContext<TProxy> {
    /**
     * The proxy for invoking the server.
     *
     * @return the proxy
     */
    TProxy proxy();

    /**
     * The session id.
     *
     * @return the id
     */
    long id();

    /**
     * Whether the transport encrypts the connection.
     *
     * @return true for TLS, {@code wss://} and {@code https://}
     */
    boolean isSecure();

    /**
     * The remote address.
     *
     * @return the address, or null when unknown
     */
    @Nullable String remoteAddress();

    /**
     * The remote port.
     *
     * @return the port, or -1 when unknown
     */
    int remotePort();

    /**
     * The server certificate of a TLS connection.
     *
     * @return the certificate, or null for a plaintext connection
     */
    @Nullable X509Certificate remoteCertificate();

    /**
     * Creates a pipe to pass to a server method.
     *
     * @return the pipe
     */
    NexusDuplexPipe createPipe();

    /**
     * Creates a typed channel to pass to a server method.
     *
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     */
    <T> NexusDuplexChannel<T> createChannel(Formatter<T> itemFormatter);

    /**
     * Disconnects the session.
     *
     * @return a future that completes when disconnected
     */
    CompletableFuture<Void> disconnect();
}
