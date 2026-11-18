package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.client.ClientSessionContext;
import com.dtronix.nexnet.serialization.MsgPackReader;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * Runtime access to the protected members of {@link ClientNexusBase}. For the NexNet runtime only; neither user code
 * nor generated code calls it.
 */
public final class ClientNexusHooks {
    private ClientNexusHooks() {
    }

    /**
     * Binds the nexus to a session, or unbinds it with null.
     *
     * @param nexus the nexus
     * @param context the session context, or null
     * @param <TProxy> the proxy type
     */
    public static <TProxy> void bind(ClientNexusBase<TProxy> nexus, @Nullable ClientSessionContext<TProxy> context) {
        nexus.bind(context);
    }

    /**
     * Calls {@link ClientNexusBase#onConnected(boolean)}.
     *
     * @param nexus the nexus
     * @param isReconnected true after a reconnection
     * @return the hook's future
     */
    public static CompletableFuture<Void> connected(ClientNexusBase<?> nexus, boolean isReconnected) {
        return nexus.onConnected(isReconnected);
    }

    /**
     * Calls {@link ClientNexusBase#onDisconnected()}.
     *
     * @param nexus the nexus
     * @return the hook's future
     */
    public static CompletableFuture<Void> disconnected(ClientNexusBase<?> nexus) {
        return nexus.onDisconnected();
    }

    /**
     * Calls {@link ClientNexusBase#onReconnecting()}.
     *
     * @param nexus the nexus
     * @return the hook's future
     */
    public static CompletableFuture<Void> reconnecting(ClientNexusBase<?> nexus) {
        return nexus.onReconnecting();
    }

    /**
     * Calls {@link ClientNexusBase#separatePool(int)}.
     *
     * @param nexus the nexus
     * @param methodId the method id
     * @return true for a separate-pool method
     */
    public static boolean separatePool(ClientNexusBase<?> nexus, int methodId) {
        return nexus.separatePool(methodId);
    }

    /**
     * Calls {@link ClientNexusBase#dispatch(int, MsgPackReader, InvocationContext)}.
     *
     * @param nexus the nexus
     * @param methodId the method id
     * @param arguments the argument reader
     * @param context the invocation
     * @return the dispatch future
     */
    public static CompletableFuture<Void> dispatch(ClientNexusBase<?> nexus, int methodId, MsgPackReader arguments,
                                                   InvocationContext context) {
        return nexus.dispatch(methodId, arguments, context);
    }
}
