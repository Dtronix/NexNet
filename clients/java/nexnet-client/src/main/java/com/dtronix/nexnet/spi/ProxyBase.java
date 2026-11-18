package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * Base class of generated server proxies ({@code <Server>Proxy extends ProxyBase}). Each generated method calls one
 * of the protected methods below with the method id from the IR. A generated proxy has one constructor taking a
 * {@link ProxyInvoker}, which the runtime supplies.
 *
 * <p>Return-kind mapping: C# {@code void} uses {@link #invokeVoid}, {@code ValueTask} uses {@link #invokeTask} and
 * {@code ValueTask<T>} uses {@link #invoke}. Methods with a pipe or channel parameter use {@link #invokePipe} or
 * {@link #invokeChannel}; they are sent with {@code IgnoreReturn | DuplexPipe}.
 *
 * <p>Cancellation: a token canceled before the invocation is sent fails the future with a
 * {@link java.util.concurrent.CancellationException} and nothing is sent. Canceling after send, through the token or
 * {@code future.cancel(true)}, sends {@code InvocationCancellation} and completes the future canceled at once.
 */
public abstract class ProxyBase {
    private final ProxyInvoker invoker;

    /**
     * Creates the proxy.
     *
     * @param invoker the session side, supplied by the runtime
     */
    protected ProxyBase(ProxyInvoker invoker) {
        if (invoker == null) {
            throw new NullPointerException("invoker");
        }
        this.invoker = invoker;
    }

    /**
     * Invokes a method that returns a value ({@code ValueTask<T>}).
     *
     * @param methodId the method id
     * @param arguments the argument writer, or {@link ArgumentWriter#NONE}
     * @param result the result formatter
     * @param cancellationToken the token, or null when the method takes none
     * @param <R> the result type
     * @return a future of the result; fails with {@link com.dtronix.nexnet.NexusRemoteInvocationException},
     *     {@link com.dtronix.nexnet.NexusUnauthorizedException} or a transport exception
     */
    protected final <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                                    @Nullable CancellationToken cancellationToken) {
        return invoker.invoke(methodId, arguments, result, cancellationToken);
    }

    /**
     * Invokes a method that completes without a value ({@code ValueTask}).
     *
     * @param methodId the method id
     * @param arguments the argument writer, or {@link ArgumentWriter#NONE}
     * @param cancellationToken the token, or null when the method takes none
     * @return a future that completes when the server method completes
     */
    protected final CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                                       @Nullable CancellationToken cancellationToken) {
        return invoker.invokeTask(methodId, arguments, cancellationToken);
    }

    /**
     * Invokes a fire-and-forget method (C# {@code void}). It still waits for invocation credit before sending; send
     * failures are logged, not thrown.
     *
     * @param methodId the method id
     * @param arguments the argument writer, or {@link ArgumentWriter#NONE}
     */
    protected final void invokeVoid(int methodId, ArgumentWriter arguments) {
        invoker.invokeVoid(methodId, arguments);
    }

    /**
     * Invokes a method with a duplex pipe parameter.
     *
     * @param methodId the method id
     * @param pipe the pipe from {@code createPipe()}
     * @param arguments the argument writer
     * @return a future that completes when the invocation is sent; use {@link NexusDuplexPipe#ready()} to wait for
     *     the server side
     */
    protected final CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe,
                                                       PipeArgumentWriter arguments) {
        return invoker.invokePipe(methodId, pipe, arguments);
    }

    /**
     * Invokes a method with a typed channel parameter.
     *
     * @param methodId the method id
     * @param channel the channel from {@code createChannel(formatter)}
     * @param arguments the argument writer
     * @param <T> the item type
     * @return a future that completes when the invocation is sent
     */
    protected final <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                              PipeArgumentWriter arguments) {
        return invoker.invokeChannel(methodId, channel, arguments);
    }

    /**
     * The synchronized list for a collection of the server nexus. Generated accessors call this; the same instance is
     * returned on every call.
     *
     * @param collectionId the collection id
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the list
     */
    protected final <T> NexusList<T> list(int collectionId, Formatter<T> itemFormatter) {
        return invoker.list(collectionId, itemFormatter);
    }
}
