package com.dtronix.nexnet.client;

import com.dtronix.nexnet.DisconnectReason;
import org.jspecify.annotations.Nullable;

/**
 * The result of {@link NexusClient#tryConnect()}.
 */
public final class ConnectionResult {
    /** The outcome of a connection attempt. */
    public enum Status {
        /** Connected. */
        SUCCESS,
        /** The server disconnected the client before the handshake completed. */
        DISCONNECTED,
        /** The attempt failed with an exception. */
        EXCEPTION,
        /** The attempt was canceled. */
        CANCELED
    }

    private final Status status;
    private final DisconnectReason disconnectReason;
    private final @Nullable Throwable exception;

    /**
     * Creates a result.
     *
     * @param status the outcome
     * @param disconnectReason why the attempt failed, or {@link DisconnectReason#NONE}
     * @param exception the failure, or null
     */
    public ConnectionResult(Status status, DisconnectReason disconnectReason, @Nullable Throwable exception) {
        if (status == null || disconnectReason == null) {
            throw new NullPointerException(status == null ? "status" : "disconnectReason");
        }
        this.status = status;
        this.disconnectReason = disconnectReason;
        this.exception = exception;
    }

    /**
     * The outcome.
     *
     * @return the status
     */
    public Status status() {
        return status;
    }

    /**
     * Whether the client connected.
     *
     * @return true for {@link Status#SUCCESS}
     */
    public boolean isSuccess() {
        return status == Status.SUCCESS;
    }

    /**
     * Why the attempt failed.
     *
     * @return the reason, or {@link DisconnectReason#NONE}
     */
    public DisconnectReason disconnectReason() {
        return disconnectReason;
    }

    /**
     * The failure.
     *
     * @return the exception, or null
     */
    public @Nullable Throwable exception() {
        return exception;
    }

    @Override
    public String toString() {
        return "ConnectionResult[" + status + ", " + disconnectReason + (exception == null ? "" : ", " + exception) + "]";
    }
}
