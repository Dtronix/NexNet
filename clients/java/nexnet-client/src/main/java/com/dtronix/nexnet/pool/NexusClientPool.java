package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.client.NexusClient;
import java.util.concurrent.CompletableFuture;
import java.util.function.Supplier;

/**
 * A pool of connected clients, like the C# {@code NexusClientPool}. Implemented by K10; this is the K01 skeleton.
 *
 * @param <TProxy> the generated server proxy type
 */
public final class NexusClientPool<TProxy> implements AutoCloseable {
    /**
     * Creates a pool.
     *
     * @param config the pool configuration
     * @param clientFactory creates a new, unconnected client (for example, {@code () -> ChatClient.create(config,
     *                      new MyNexus())})
     */
    public NexusClientPool(NexusClientPoolConfig config, Supplier<NexusClient<TProxy>> clientFactory) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Rents a connected client, connecting a new one when none is idle and the pool is not full.
     *
     * @return a future of the rented client
     */
    public CompletableFuture<RentedNexusClient<TProxy>> rent() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Rents a connected client, with cancellation.
     *
     * @param cancellationToken cancels the wait
     * @return a future of the rented client
     */
    public CompletableFuture<RentedNexusClient<TProxy>> rent(CancellationToken cancellationToken) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The maximum number of connections.
     *
     * @return the maximum
     */
    public int maxConnections() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The number of idle connections.
     *
     * @return the number
     */
    public int availableConnections() {
        throw new UnsupportedOperationException("not implemented");
    }

    /** Disconnects every client. Idempotent. */
    @Override
    public void close() {
        throw new UnsupportedOperationException("not implemented");
    }
}
