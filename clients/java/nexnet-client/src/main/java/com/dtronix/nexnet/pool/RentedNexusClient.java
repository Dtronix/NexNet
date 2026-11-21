package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.client.ConnectionState;
import java.util.concurrent.CompletableFuture;

/**
 * A client rented from a {@link NexusClientPool}. Close it to return the client to the pool.
 *
 * @param <TProxy> the generated server proxy type
 */
public interface RentedNexusClient<TProxy> extends AutoCloseable {
    /**
     * The proxy for invoking the server.
     *
     * @return the proxy
     * @throws IllegalStateException after the client is returned
     */
    TProxy proxy();

    /**
     * The connection state.
     *
     * @return the state
     */
    ConnectionState state();

    /**
     * Completes when the connection closes.
     *
     * @return the future
     */
    CompletableFuture<Void> disconnected();

    /**
     * Reconnects if the connection was lost.
     *
     * @return a future of whether the client is connected
     */
    CompletableFuture<Boolean> ensureConnected();

    /** Returns the client to the pool. Idempotent. */
    @Override
    void close();
}
