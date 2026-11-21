package com.dtronix.nexnet.reconnect;

import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import java.util.concurrent.CompletableFuture;

/**
 * What a {@link ReconnectController} needs of a client. The NexNet client implements it internally; tests and
 * custom clients can implement it too.
 */
public interface ReconnectableClient {
    /**
     * The connection state.
     *
     * @return the state
     */
    ConnectionState state();

    /**
     * Runs the client nexus's {@code onReconnecting()} hook, once per reconnection loop, before the first attempt.
     *
     * @return a future that completes when the hook has run
     */
    CompletableFuture<Void> onReconnecting();

    /**
     * Makes one reconnection attempt: the greeting is flagged as a reconnection and the nexus's
     * {@code onConnected(true)} runs on success. Must not do IO on the calling thread.
     *
     * @return a future of the result
     */
    CompletableFuture<ConnectionResult> reconnect();
}
