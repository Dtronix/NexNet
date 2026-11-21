package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.collections.NexusCollectionMode;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * The session side of a proxy: what {@link ProxyBase} delegates to. Implemented by the invocation runtime (K07);
 * generated code never calls it directly. See {@link ProxyBase} for the semantics of each method.
 */
public interface ProxyInvoker {
    /**
     * See {@link ProxyBase#invoke(int, ArgumentWriter, Formatter, CancellationToken)}.
     *
     * @param methodId the method id
     * @param arguments the argument writer
     * @param result the result formatter
     * @param cancellationToken the token, or null
     * @param <R> the result type
     * @return the result future
     */
    <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                    @Nullable CancellationToken cancellationToken);

    /**
     * See {@link ProxyBase#invokeTask(int, ArgumentWriter, CancellationToken)}.
     *
     * @param methodId the method id
     * @param arguments the argument writer
     * @param cancellationToken the token, or null
     * @return the completion future
     */
    CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                       @Nullable CancellationToken cancellationToken);

    /**
     * See {@link ProxyBase#invokeVoid(int, ArgumentWriter)}.
     *
     * @param methodId the method id
     * @param arguments the argument writer
     */
    void invokeVoid(int methodId, ArgumentWriter arguments);

    /**
     * See {@link ProxyBase#invokePipe(int, NexusDuplexPipe, PipeArgumentWriter)}.
     *
     * @param methodId the method id
     * @param pipe the pipe
     * @param arguments the argument writer
     * @return the send future
     */
    CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments);

    /**
     * See {@link ProxyBase#invokeChannel(int, NexusDuplexChannel, PipeArgumentWriter)}.
     *
     * @param methodId the method id
     * @param channel the channel
     * @param arguments the argument writer
     * @param <T> the item type
     * @return the send future
     */
    <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                              PipeArgumentWriter arguments);

    /**
     * See {@link ProxyBase#list(int, NexusCollectionMode, Formatter)}.
     *
     * @param collectionId the collection id
     * @param mode the collection's mode, from its {@code [NexusCollection]} attribute (amendment C-4)
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the list
     */
    <T> NexusList<T> list(int collectionId, NexusCollectionMode mode, Formatter<T> itemFormatter);
}
