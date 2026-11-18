package com.dtronix.nexnet.client;

/**
 * Receives connection state changes, on the client's callback executor.
 */
@FunctionalInterface
public interface StateListener {
    /**
     * Called after the state changes.
     *
     * @param state the new state
     */
    void onStateChanged(ConnectionState state);
}
