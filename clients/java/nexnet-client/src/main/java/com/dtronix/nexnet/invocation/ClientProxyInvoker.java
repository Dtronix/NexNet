package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.collections.NexusCollectionMode;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import org.jspecify.annotations.Nullable;

/**
 * The invoker a client's proxy is created with: it lasts as long as the client and forwards to the
 * {@link InvocationRuntime} of the current session. Without one, calls fail with an {@link IllegalStateException}
 * ("not connected"), like the C# proxy.
 */
public final class ClientProxyInvoker implements ProxyInvoker {
    private final Executor callbackExecutor;
    private volatile @Nullable InvocationRuntime runtime;

    /**
     * Creates the invoker.
     *
     * @param callbackExecutor the executor that completes failed futures
     */
    public ClientProxyInvoker(Executor callbackExecutor) {
        if (callbackExecutor == null) {
            throw new NullPointerException("callbackExecutor");
        }
        this.callbackExecutor = callbackExecutor;
    }

    /**
     * Routes calls to a session's runtime.
     *
     * @param runtime the runtime
     */
    public synchronized void attach(InvocationRuntime runtime) {
        this.runtime = runtime;
    }

    /**
     * Stops routing calls to a runtime, if it is the current one.
     *
     * @param runtime the runtime
     */
    public synchronized void detach(InvocationRuntime runtime) {
        if (this.runtime == runtime) {
            this.runtime = null;
        }
    }

    /**
     * The current runtime.
     *
     * @return the runtime, or null when not connected
     */
    public @Nullable InvocationRuntime current() {
        return runtime;
    }

    private static IllegalStateException notConnected() {
        return new IllegalStateException("The client is not connected.");
    }

    private <T> CompletableFuture<T> failed() {
        CompletableFuture<T> future = new CompletableFuture<>();
        IllegalStateException error = notConnected();
        NexusExecutors.execute(callbackExecutor, () -> future.completeExceptionally(error));
        return future;
    }

    @Override
    public <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                           @Nullable CancellationToken cancellationToken) {
        InvocationRuntime current = runtime;
        return current == null ? failed() : current.invoke(methodId, arguments, result, cancellationToken);
    }

    @Override
    public CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                              @Nullable CancellationToken cancellationToken) {
        InvocationRuntime current = runtime;
        return current == null ? failed() : current.invokeTask(methodId, arguments, cancellationToken);
    }

    @Override
    public void invokeVoid(int methodId, ArgumentWriter arguments) {
        InvocationRuntime current = runtime;
        if (current == null) {
            throw notConnected();
        }
        current.invokeVoid(methodId, arguments);
    }

    @Override
    public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
        InvocationRuntime current = runtime;
        return current == null ? failed() : current.invokePipe(methodId, pipe, arguments);
    }

    @Override
    public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                     PipeArgumentWriter arguments) {
        InvocationRuntime current = runtime;
        return current == null ? failed() : current.invokeChannel(methodId, channel, arguments);
    }

    @Override
    public <T> NexusList<T> list(int collectionId, NexusCollectionMode mode, Formatter<T> itemFormatter) {
        throw new UnsupportedOperationException("not implemented");
    }
}
