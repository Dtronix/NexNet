package com.dtronix.nexnet;

/**
 * Base class of the exceptions thrown by the NexNet runtime.
 */
public class NexusException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    /**
     * Creates the exception.
     *
     * @param message the detail message
     */
    public NexusException(String message) {
        super(message);
    }

    /**
     * Creates the exception.
     *
     * @param message the detail message
     * @param cause the cause, or null
     */
    public NexusException(String message, Throwable cause) {
        super(message, cause);
    }
}
