package com.dtronix.nexnet.invocation;

import org.jspecify.annotations.Nullable;

/**
 * An application error sent to the caller of a failed client nexus method in place of the exception's detail, like
 * the C# {@code NexusRemoteError}. Returned from {@code ClientNexusBase.onInvocationException}; the server sees it as
 * the remote error code and message. Either may be null, as in C# ({@code int? Code}, {@code string? Message}): an
 * error with only a message has a null code.
 */
public final class NexusRemoteError {
    private final @Nullable Integer code;
    private final @Nullable String message;

    /**
     * Creates the error.
     *
     * @param code the application error code, or null for none
     * @param message the message, or null
     */
    public NexusRemoteError(@Nullable Integer code, @Nullable String message) {
        this.code = code;
        this.message = message;
    }

    /**
     * The application error code.
     *
     * @return the code, or null when the error has none
     */
    public @Nullable Integer code() {
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
