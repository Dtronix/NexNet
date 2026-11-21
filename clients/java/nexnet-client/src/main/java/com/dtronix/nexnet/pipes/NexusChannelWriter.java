package com.dtronix.nexnet.pipes;

import java.util.concurrent.CompletableFuture;

/**
 * Writes typed items to a channel.
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
     * Writes several items with one flush. When an item is larger than the server accepts
     * ({@link com.dtronix.nexnet.NexusMessageTooLargeException}) or the formatter can't write it, the items before
     * it are still sent, as in C#; it and the items after it are not, and the future fails. The channel stays usable.
     *
     * @param items the items
     * @return a future that completes when all items are queued, or fails once the items before a bad one are
     */
    CompletableFuture<Void> writeAll(Iterable<? extends T> items);

    /**
     * Completes the channel. The reader sees the end after the remaining items.
     *
     * @return a future that completes when the completion is sent
     */
    CompletableFuture<Void> complete();

    /**
     * Writes the items, then completes the channel. A bad item fails it as in {@link #writeAll(Iterable)}, and the
     * channel is then not completed.
     *
     * @param items the items
     * @return a future that completes when the completion is sent
     */
    CompletableFuture<Void> writeAndComplete(Iterable<? extends T> items);
}
