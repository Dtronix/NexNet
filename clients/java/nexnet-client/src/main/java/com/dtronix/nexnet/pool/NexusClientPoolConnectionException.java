package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.client.ConnectionResult;

/**
 * A pool could not connect a new client for a rent (C# {@code ClientPoolConnectionException}).
 */
public final class NexusClientPoolConnectionException extends NexusException {
    private static final long serialVersionUID = 1L;

    private final transient ConnectionResult result;

    /**
     * Creates the exception.
     *
     * @param message the message
     * @param result the failed connection result
     */
    public NexusClientPoolConnectionException(String message, ConnectionResult result) {
        super(message + " " + result, result.exception() == null ? new IllegalStateException(result.toString())
            : result.exception());
        this.result = result;
    }

    /**
     * The failed connection result.
     *
     * @return the result
     */
    public ConnectionResult result() {
        return result;
    }
}
