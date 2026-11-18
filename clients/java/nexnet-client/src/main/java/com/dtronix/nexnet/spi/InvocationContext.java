package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CompletableFuture;

/**
 * One incoming invocation, as seen by a generated {@code dispatch} method. Implemented by the invocation runtime
 * (K07).
 *
 * <p>A generated {@code dispatch} reads the arguments, calls the user's method and returns one of
 * {@link #result(CompletableFuture, Formatter)}, {@link #result(CompletableFuture)} or {@link #noResult()}.
 */
public interface InvocationContext {
    /**
     * The token canceled when the server cancels this invocation or the session closes. Pass it to user methods
     * that take a {@code CancellationToken}.
     *
     * @return the token
     */
    CancellationToken cancellationToken();

    /**
     * The pipe for a pipe argument.
     *
     * @param initialPipeId the id read from the arguments
     * @return the pipe
     */
    NexusDuplexPipe pipe(int initialPipeId);

    /**
     * The channel for a channel argument.
     *
     * @param initialPipeId the id read from the arguments
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     */
    <T> NexusDuplexChannel<T> channel(int initialPipeId, Formatter<T> itemFormatter);

    /**
     * Sends the value of a {@code ValueTask<T>} method when it completes, or its failure as a remote error.
     *
     * @param result the user's future
     * @param formatter the result formatter
     * @param <R> the result type
     * @return a future that completes when the invocation is finished
     */
    <R> CompletableFuture<Void> result(CompletableFuture<R> result, Formatter<R> formatter);

    /**
     * Sends the completion of a {@code ValueTask} method, or its failure as a remote error.
     *
     * @param result the user's future
     * @return a future that completes when the invocation is finished
     */
    CompletableFuture<Void> result(CompletableFuture<Void> result);

    /**
     * Finishes a fire-and-forget ({@code void}) or pipe method, which sends no result.
     *
     * @return a completed future
     */
    CompletableFuture<Void> noResult();
}
