package com.dtronix.nexnet.invocation;

import org.jspecify.annotations.Nullable;

/**
 * An application error sent to the caller of a failed client nexus method in place of the exception's detail, like
 * the C# {@code NexusRemoteError}. Returned from {@code ClientNexusBase.onInvocationException}; the server sees it as
 * the remote error code and message.
 */
public final class NexusRemoteError {
    private final int code;
    private final @Nullable String message;

    /**
     * Creates the error.
     *
     * @param code the application error code
     * @param message the message, or null
     */
    public NexusRemoteError(int code, @Nullable String message) {
        this.code = code;
        this.message = message;
    }

    /**
     * The application error code.
     *
     * @return the code
     */
    public int code() {
        return code;
    }

    /**
     * The message.
     *
     * @return the message, or null
     */
    public @Nullable String message() {
        return message;
    }
}
