package com.dtronix.nexnet.pipes;

import java.util.concurrent.CompletableFuture;

/**
 * Writes typed items to a channel. Implemented by K08.
 *
 * @param <T> the item type
 */
public interface NexusChannelWriter<T> {
    /**
     * Writes one item.
     *
     * @param item the item
     * @return a future that completes when the item is within the peer's credit and queued
     */
    CompletableFuture<Void> write(T item);

    /**
     * Writes several items with one flush.
     *
     * @param items the items
     * @return a future that completes when all items are queued
     */
    CompletableFuture<Void> writeAll(Iterable<? extends T> items);

    /**
     * Completes the channel. The reader sees the end after the remaining items.
     *
     * @return a future that completes when the completion is sent
     */
    CompletableFuture<Void> complete();

    /**
     * Writes the items, then completes the channel.
     *
     * @param items the items
     * @return a future that completes when the completion is sent
     */
    CompletableFuture<Void> writeAndComplete(Iterable<? extends T> items);
}
