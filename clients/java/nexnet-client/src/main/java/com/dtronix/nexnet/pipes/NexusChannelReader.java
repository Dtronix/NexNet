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
     * Reads the next item, for item types that are never null. An empty optional means the channel is complete.
     * For a nullable item type use {@link #readItem()}, which tells a null item apart from the end of the channel.
     *
     * @return a future of the item, or of an empty optional when the channel is complete; it fails with a
     *     {@link NullPointerException} when the item read is null (the item is consumed)
     */
    CompletableFuture<Optional<T>> read();

    /**
     * Reads the next item, which may be null, or the end of the channel.
     *
     * @return a future of the result: {@link ChannelReadResult#isCompleted()} at the end of the channel, otherwise
     *     the item in {@link ChannelReadResult#value()}
     */
    CompletableFuture<ChannelReadResult<T>> readItem();

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
