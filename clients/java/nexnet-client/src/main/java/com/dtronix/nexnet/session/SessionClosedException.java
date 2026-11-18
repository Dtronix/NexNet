package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusException;

/**
 * The session closed before an operation completed: a send that was still queued, or a handshake that did not
 * finish.
 */
public class SessionClosedException extends NexusException {
    private static final long serialVersionUID = 1L;

    /** Why the session closed. */
    private final DisconnectReason reason;

    /**
     * Creates the exception.
     *
     * @param reason why the session closed
     */
    public SessionClosedException(DisconnectReason reason) {
        super("The session closed: " + reason + ".");
        this.reason = reason;
    }

    /**
     * Why the session closed.
     *
     * @return the reason
     */
    public DisconnectReason reason() {
        return reason;
    }
}
