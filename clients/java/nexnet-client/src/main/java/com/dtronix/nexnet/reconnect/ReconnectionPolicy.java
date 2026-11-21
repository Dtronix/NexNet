package com.dtronix.nexnet.reconnect;

import com.dtronix.nexnet.DisconnectReason;

/**
 * Decides whether and when a client reconnects after losing its connection, like the C# {@code IReconnectionPolicy}.
 */
public interface ReconnectionPolicy {
    /** Returned by {@link #reconnectDelayMillis(int)} to stop reconnecting. */
    long STOP = -1;

    /**
     * The delay before a reconnection attempt.
     *
     * @param retryAttempt the attempt number, starting at 0
     * @return the delay in milliseconds, or {@link #STOP} to give up
     */
    long reconnectDelayMillis(int retryAttempt);

    /**
     * Whether to reconnect after a disconnect for this reason. By default, reconnects for
     * {@link DisconnectReason#SOCKET_ERROR}, {@link DisconnectReason#TIMEOUT} and
     * {@link DisconnectReason#SERVER_RESTARTING}.
     *
     * @param reason why the connection closed
     * @return true to reconnect
     */
    default boolean shouldReconnect(DisconnectReason reason) {
        return reason == DisconnectReason.SOCKET_ERROR
            || reason == DisconnectReason.TIMEOUT
            || reason == DisconnectReason.SERVER_RESTARTING;
    }
}
