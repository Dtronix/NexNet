package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.CancellationToken;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/**
 * Reads typed items from a channel. Reads are served one at a time, in call order.
 *
 * <p>A read can be canceled, like a C# {@code ReadAsync(cancellationToken)}: by its {@link CancellationToken}, or by
 * cancelling the future it returns ({@code future.cancel(true)}). A read canceled before it takes an item fails with a
 * {@link java.util.concurrent.CancellationException} and consumes nothing, so the next read gets the items that
 * arrive later. A read that has already taken its items is not canceled.
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
     * {@link #read()} that {@code cancellationToken} can cancel (see the class description).
     *
     * @param cancellationToken cancels the read until it takes an item; a token canceled already fails the read at
     *     once
     * @return a future of the item, or of an empty optional when the channel is complete
     */
    default CompletableFuture<Optional<T>> read(CancellationToken cancellationToken) {
        if (!cancellationToken.canBeCanceled()) {
            return read();
        }
        throw new UnsupportedOperationException("This channel reader cannot cancel a read.");
    }

    /**
     * Reads the next item, which may be null, or the end of the channel.
     *
     * @return a future of the result: {@link ChannelReadResult#isCompleted()} at the end of the channel, otherwise
     *     the item in {@link ChannelReadResult#value()}
     */
    CompletableFuture<ChannelReadResult<T>> readItem();

    /**
     * {@link #readItem()} that {@code cancellationToken} can cancel (see the class description).
     *
     * @param cancellationToken cancels the read until it takes an item; a token canceled already fails the read at
     *     once
     * @return a future of the result
     */
    default CompletableFuture<ChannelReadResult<T>> readItem(CancellationToken cancellationToken) {
        if (!cancellationToken.canBeCanceled()) {
            return readItem();
        }
        throw new UnsupportedOperationException("This channel reader cannot cancel a read.");
    }

    /**
     * Reads up to {@code max} items that are available, waiting for at least one.
     *
     * @param max the maximum number of items
     * @return a future of the items; empty when the channel is complete
     */
    CompletableFuture<List<T>> readBatch(int max);

    /**
     * {@link #readBatch(int)} that {@code cancellationToken} can cancel (see the class description).
     *
     * @param max the maximum number of items
     * @param cancellationToken cancels the read until it takes items; a token canceled already fails the read at once
     * @return a future of the items; empty when the channel is complete
     */
    default CompletableFuture<List<T>> readBatch(int max, CancellationToken cancellationToken) {
        if (!cancellationToken.canBeCanceled()) {
            return readBatch(max);
        }
        throw new UnsupportedOperationException("This channel reader cannot cancel a read.");
    }

    /**
     * Reads every item until the channel completes. Cancelling its future does not stop it: the items it reads
     * afterwards are consumed and dropped.
     *
     * @return a future of all items
     */
    CompletableFuture<List<T>> readUntilComplete();
}
