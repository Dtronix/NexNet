package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;

/**
 * Receives the messages the session core does not handle itself: everything after the connection completes except
 * {@code Ping} and disconnects. The invocation (K07), pipe (K08) and collection (K09) runtimes plug in here.
 *
 * <p>Called on the reader thread, in wire order. A handler must not block and must never complete a user-visible
 * future on this thread: it hands user work to an executor. To end the session for a protocol violation it throws
 * {@link SessionProtocolException}.
 */
public interface MessageHandler {
    /** A handler that refuses every message with {@code ProtocolError}. The default until the invocation runtime is attached. */
    MessageHandler UNHANDLED = frame -> {
        throw new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR,
            "No handler for message type " + frame.type() + ".");
    };

    /**
     * Handles one message.
     *
     * @param frame the message
     * @throws SessionProtocolException to end the session with the exception's reason
     */
    void onMessage(InboundFrame frame);

    /**
     * Called once when the session has closed, on the thread that closed it. Fail outstanding work here (through an
     * executor for anything user-visible).
     *
     * @param reason why the session ended
     */
    default void onClosed(DisconnectReason reason) {
    }
}
