package com.dtronix.nexnet.pipes;

import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/**
 * Reads typed items from a channel.
 *
 * @param <T> the item type
 */
public interface NexusChannelReader<T> {
    /**
     * Reads the next item.
     *
     * @return a future of the item, or of an empty optional when the channel is complete
     */
    CompletableFuture<Optional<T>> read();

    /**
     * Reads up to {@code max} items that are available, waiting for at least one.
     *
     * @param max the maximum number of items
     * @return a future of the items; empty when the channel is complete
     */
    CompletableFuture<List<T>> readBatch(int max);

    /**
     * Reads every item until the channel completes.
     *
     * @return a future of all items
     */
    CompletableFuture<List<T>> readUntilComplete();
}
