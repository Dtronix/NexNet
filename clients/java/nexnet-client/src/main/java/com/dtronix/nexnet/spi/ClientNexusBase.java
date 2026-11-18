package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.client.ClientSessionContext;
import com.dtronix.nexnet.serialization.MsgPackReader;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * Base class of client nexus implementations. The generator emits {@code <Client>NexusBase extends
 * ClientNexusBase<ServerProxy>} with an abstract method per client nexus method and the {@link #dispatch} switch; the
 * user extends that class.
 *
 * <p>The lifecycle hooks run on the invocation executor, never on the reader thread.
 *
 * @param <TProxy> the generated server proxy type
 */
public abstract class ClientNexusBase<TProxy> {
    private volatile @Nullable ClientSessionContext<TProxy> context;

    /** Creates the nexus. */
    protected ClientNexusBase() {
    }

    /**
     * The current session.
     *
     * @return the context
     * @throws IllegalStateException when the client has not connected yet
     */
    public final ClientSessionContext<TProxy> context() {
        ClientSessionContext<TProxy> current = context;
        if (current == null) {
            throw new IllegalStateException("The client has not connected yet.");
        }
        return current;
    }

    /**
     * Called after the handshake completes.
     *
     * @param isReconnected true when this connection replaces a lost one
     * @return a future the runtime waits for before delivering invocations
     */
    protected CompletableFuture<Void> onConnected(boolean isReconnected) {
        return CompletableFuture.completedFuture(null);
    }

    /**
     * Called after the connection closes.
     *
     * @return a future the runtime waits for
     */
    protected CompletableFuture<Void> onDisconnected() {
        return CompletableFuture.completedFuture(null);
    }

    /**
     * Called when the connection is lost and the client starts reconnecting.
     *
     * @return a future the runtime waits for
     */
    protected CompletableFuture<Void> onReconnecting() {
        return CompletableFuture.completedFuture(null);
    }

    /**
     * Runs one incoming invocation: reads the arguments, calls the method and returns the result through the
     * context. Generated; the runtime has already checked the id against the {@link HandshakeDescriptor}.
     *
     * @param methodId the method id
     * @param arguments the reader over the serialized arguments
     * @param context the invocation
     * @return the future returned by one of the {@link InvocationContext} result methods
     * @throws IllegalArgumentException for an unknown method id
     */
    protected abstract CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments,
                                                        InvocationContext context);

    /**
     * Whether a method runs in the separate invocation pool ({@code [NexusMethod(SeparatePool = true)]}). Generated
     * code overrides this when the client nexus has such methods.
     *
     * @param methodId the method id
     * @return true for a separate-pool method
     */
    protected boolean separatePool(int methodId) {
        return false;
    }

    final void bind(@Nullable ClientSessionContext<TProxy> context) {
        this.context = context;
    }
}
