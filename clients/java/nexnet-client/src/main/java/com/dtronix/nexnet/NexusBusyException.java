package com.dtronix.nexnet;

/**
 * The session cannot accept the operation now, for example because the large-message budget is in use.
 */
public class NexusBusyException extends NexusException {
    private static final long serialVersionUID = 1L;

    /**
     * Creates the exception.
     *
     * @param message the detail message
     */
    public NexusBusyException(String message) {
        super(message);
    }
}
