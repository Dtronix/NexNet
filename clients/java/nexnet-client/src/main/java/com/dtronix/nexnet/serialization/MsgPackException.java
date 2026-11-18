package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.NexusException;

/**
 * MessagePack input is malformed, of an unexpected type or over a configured limit, or a value cannot be written.
 */
public class MsgPackException extends NexusException {
    private static final long serialVersionUID = 1L;

    /**
     * Creates the exception.
     *
     * @param message the detail message
     */
    public MsgPackException(String message) {
        super(message);
    }

    /**
     * Creates the exception.
     *
     * @param message the detail message
     * @param cause the cause, or null
     */
    public MsgPackException(String message, Throwable cause) {
        super(message, cause);
    }
}
