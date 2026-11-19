package com.dtronix.nexnet.reconnect;

import com.dtronix.nexnet.DisconnectReason;

/**
 * Receives the events of a {@link ReconnectController}, on its executor, in order. Every method does nothing by
 * default.
 */
public interface ReconnectListener {
    /**
     * A reconnection loop started after the session was lost.
     *
     * @param reason why the session was lost
     */
    default void onReconnecting(DisconnectReason reason) {
    }

    /**
     * An attempt starts.
     *
     * @param attempt the attempt number, starting at 1
     */
    default void onAttempt(int attempt) {
    }

    /**
     * The client reconnected.
     *
     * @param attempts the attempts the loop made
     */
    default void onReconnected(int attempts) {
    }

    /**
     * The policy stopped reconnecting.
     *
     * @param reason why the last attempt failed (or the session was lost)
     * @param attempts the attempts the loop made
     */
    default void onReconnectFailed(DisconnectReason reason, int attempts) {
    }
}
