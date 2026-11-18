package com.dtronix.nexnet;

/**
 * The transport failed to connect, or failed while connected.
 */
public class NexusTransportException extends NexusException {
    private static final long serialVersionUID = 1L;

    /** The failure kind. */
    private final TransportError error;

    /**
     * Creates the exception.
     *
     * @param error the failure kind
     * @param message the detail message
     * @param cause the cause, or null
     */
    public NexusTransportException(TransportError error, String message, Throwable cause) {
        super(message, cause);
        this.error = error;
    }

    /**
     * The failure kind.
     *
     * @return the error
     */
    public TransportError error() {
        return error;
    }
}
