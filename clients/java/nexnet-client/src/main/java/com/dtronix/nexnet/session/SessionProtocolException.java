package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusException;

/**
 * Thrown by a {@link MessageHandler} to end the session: the session sends the reason's disconnect byte and closes.
 */
public class SessionProtocolException extends NexusException {
    private static final long serialVersionUID = 1L;

    /** The disconnect reason. */
    private final DisconnectReason reason;

    /**
     * Creates the exception.
     *
     * @param reason the disconnect reason, for example {@code PROTOCOL_ERROR} or {@code FLOW_CONTROL}
     * @param message the detail message
     */
    public SessionProtocolException(DisconnectReason reason, String message) {
        super(message);
        this.reason = reason;
    }

    /**
     * The disconnect reason.
     *
     * @return the reason
     */
    public DisconnectReason reason() {
        return reason;
    }
}
