package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.Registration;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/**
 * A synchronized list ({@code INexusList<T>}) exposed by a generated proxy. The {@link List} view is read-only: its
 * mutators throw {@link UnsupportedOperationException}. Change the list with the asynchronous methods, which complete
 * with false when the server rejects the operation (for example, a stale version). The runtime implementation is
 * {@link NexusListClient}.
 *
 * @param <T> the item type
 */
public interface NexusList<T> extends List<T> {
    /**
     * Connects to the server's list and starts receiving changes.
     *
     * @return a future of true when enabled, false when the server refused
     */
    CompletableFuture<Boolean> enable();

    /**
     * Disconnects from the server's list.
     *
     * @return a future that completes when disabled and the collection's pipe is freed (the server confirmed its
     *     close), so an enable right after it finds the pipe slot available; it waits at most the progress timeout for
     *     the server's confirmation
     */
    CompletableFuture<Void> disable();

    /**
     * Completes when the initial contents have arrived.
     *
     * @return the future
     */
    CompletableFuture<Void> ready();

    /**
     * Completes when the list is disabled or its connection is lost.
     *
     * @return the future
     */
    CompletableFuture<Void> disabled();

    /**
     * The connection state.
     *
     * @return the state
     */
    NexusCollectionState state();

    /**
     * Adds an item at the end.
     *
     * @param item the item
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> addAsync(T item);

    /**
     * Inserts an item.
     *
     * @param index the index
     * @param item the item
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> insertAsync(int index, T item);

    /**
     * Removes the first item equal to {@code item} ({@link Object#equals(Object)}).
     *
     * @param item the item
     * @return a future of whether an item was removed
     */
    CompletableFuture<Boolean> removeAsync(T item);

    /**
     * Removes the item at an index.
     *
     * @param index the index
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> removeAtAsync(int index);

    /**
     * Replaces the item at an index.
     *
     * @param index the index
     * @param item the new item
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> replaceAsync(int index, T item);

    /**
     * Moves an item.
     *
     * @param fromIndex the current index
     * @param toIndex the new index
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> moveAsync(int fromIndex, int toIndex);

    /**
     * Removes every item.
     *
     * @return a future of whether the server applied the change
     */
    CompletableFuture<Boolean> clearAsync();

    /**
     * Adds a change listener, called on the callback executor.
     *
     * @param listener the listener
     * @return a registration that removes the listener
     */
    Registration addChangeListener(Consumer<NexusListChange<T>> listener);
}
