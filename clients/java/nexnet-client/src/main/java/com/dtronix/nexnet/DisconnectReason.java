package com.dtronix.nexnet;

/**
 * Why a connection closed. The values are the NexNet disconnect message types (20–38); see the protocol
 * specification. An unknown code in that range is still a disconnect and maps to {@link #UNKNOWN}.
 */
public enum DisconnectReason {
    /** No reason: the connection is not disconnected. */
    NONE(0),
    /** The socket failed. */
    SOCKET_ERROR(20),
    /** Graceful disconnection. */
    GRACEFUL(21),
    /** The peer violated the protocol. */
    PROTOCOL_ERROR(22),
    /** The connection timed out. */
    TIMEOUT(23),
    /** The client nexus does not match the server's version. */
    CLIENT_MISMATCH(24),
    /** The server nexus does not match the client's version. */
    SERVER_MISMATCH(25),
    /** The server is shutting down. */
    SERVER_SHUTDOWN(28),
    /** Authentication is required or failed. */
    AUTHENTICATION(29),
    /** The server is restarting. */
    SERVER_RESTARTING(30),
    /** The socket was closed while writing. */
    SOCKET_CLOSED_WHEN_WRITING(32),
    /** The server rejected the connection because of rate limiting. */
    RATE_LIMITED(33),
    /** An unauthorized method invocation closed the connection. */
    UNAUTHORIZED(34),
    /** The peer sent more than the credit it was granted on a pipe. */
    FLOW_CONTROL(35),
    /** The session made no progress within the progress timeout. */
    STALLED(37),
    /** A disconnect code in the reserved range 20–38 that this runtime does not know. */
    UNKNOWN(-1);

    private final int code;

    DisconnectReason(int code) {
        this.code = code;
    }

    /**
     * The wire code (the disconnect message type), or -1 for {@link #UNKNOWN}.
     *
     * @return the code
     */
    public int code() {
        return code;
    }

    /**
     * Maps a wire code to a reason.
     *
     * @param code a message type
     * @return the reason; {@link #UNKNOWN} for an unassigned code in 20–38
     * @throws IllegalArgumentException when the code is not 0 and not in 20–38
     */
    public static DisconnectReason fromCode(int code) {
        for (DisconnectReason reason : values()) {
            if (reason.code == code) {
                return reason;
            }
        }
        if (code >= 20 && code <= 38) {
            return UNKNOWN;
        }
        throw new IllegalArgumentException("Not a disconnect code: " + code);
    }
}
