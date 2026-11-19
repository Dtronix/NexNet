package com.dtronix.nexnet.pool;

import java.util.concurrent.CompletableFuture;

/**
 * Gives access to a synchronized collection through a client rented from a pool (C#
 * {@code INexusCollectionClientConnector}). {@link #close()} returns the rented client.
 *
 * @param <C> the collection type
 */
public interface NexusCollectionConnector<C> extends AutoCloseable {
    /**
     * Rents a client and selects the collection from its proxy.
     *
     * @return a future of the collection
     */
    CompletableFuture<C> collection();

    /** Returns the rented client to the pool. Idempotent. */
    @Override
    void close();
}
