package com.dtronix.nexnet.session;

/**
 * The NexNet message types (protocol specification section 3) and their framing: whether a type has a body and how
 * long its post-header is.
 */
public final class MessageTypes {
    /** Ping: one byte, no body. */
    public static final int PING = 1;
    /** The first disconnect code. */
    public static final int DISCONNECT_FIRST = 20;
    /** The last disconnect code. */
    public static final int DISCONNECT_LAST = 38;
    /** Raw pipe bytes; post-header: the pipe id ({@code [client id][server id]}). */
    public static final int DUPLEX_PIPE_WRITE = 50;
    /** Pipe credit; post-header: the pipe id. */
    public static final int PIPE_WINDOW_UPDATE = 51;
    /** The client greeting. */
    public static final int CLIENT_GREETING = 100;
    /** The client greeting sent when reconnecting. */
    public static final int CLIENT_GREETING_RECONNECTION = 101;
    /** The server greeting. */
    public static final int SERVER_GREETING = 105;
    /** An invocation. */
    public static final int INVOCATION = 110;
    /** An invocation cancellation. */
    public static final int INVOCATION_CANCELLATION = 111;
    /** An invocation result. */
    public static final int INVOCATION_RESULT = 112;
    /** The start of a large message. */
    public static final int MESSAGE_START = 113;
    /** A large-message chunk; post-header: invocation id ({@code uint16} LE) and kind. */
    public static final int MESSAGE_CHUNK = 114;
    /** A large-message abort. */
    public static final int MESSAGE_ABORT = 115;
    /** Returned invocation credit. */
    public static final int INVOCATION_CREDIT = 116;
    /** A pipe state update. */
    public static final int DUPLEX_PIPE_UPDATE_STATE = 120;

    private MessageTypes() {
    }

    /**
     * Whether a type byte is a disconnect (20–38), which is read before anything else.
     *
     * @param type the type byte
     * @return true for a disconnect code
     */
    public static boolean isDisconnect(int type) {
        return type >= DISCONNECT_FIRST && type <= DISCONNECT_LAST;
    }

    /**
     * Whether a type is defined by the protocol.
     *
     * @param type the type byte
     * @return true for a known type
     */
    public static boolean isKnown(int type) {
        return type == PING || isDisconnect(type) || postHeaderLength(type) >= 0;
    }

    /**
     * The post-header length of a type with a body.
     *
     * @param type the type
     * @return 0, 2 or 3; -1 for a type without a body or an unknown type
     */
    public static int postHeaderLength(int type) {
        switch (type) {
            case DUPLEX_PIPE_WRITE:
            case PIPE_WINDOW_UPDATE:
                return 2;
            case MESSAGE_CHUNK:
                return 3;
            case CLIENT_GREETING:
            case CLIENT_GREETING_RECONNECTION:
            case SERVER_GREETING:
            case INVOCATION:
            case INVOCATION_CANCELLATION:
            case INVOCATION_RESULT:
            case MESSAGE_START:
            case MESSAGE_ABORT:
            case INVOCATION_CREDIT:
            case DUPLEX_PIPE_UPDATE_STATE:
                return 0;
            default:
                return -1;
        }
    }
}
