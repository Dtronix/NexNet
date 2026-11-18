package com.dtronix.nexnet;

import org.jspecify.annotations.Nullable;

/**
 * A remote invocation failed on the server. The server decides how much detail it sends
 * ({@code RemoteExceptionDetail}); each field is null when the server did not send it.
 */
public class NexusRemoteInvocationException extends NexusException {
    private static final long serialVersionUID = 1L;

    private final @Nullable Integer remoteErrorCode;
    private final @Nullable String remoteExceptionType;
    private final @Nullable String remoteMessage;

    /**
     * Creates the exception.
     *
     * @param remoteErrorCode the application error code, or null
     * @param remoteExceptionType the remote exception type name, or null
     * @param remoteMessage the remote message, or null
     */
    public NexusRemoteInvocationException(@Nullable Integer remoteErrorCode, @Nullable String remoteExceptionType,
                                          @Nullable String remoteMessage) {
        this("The remote invocation failed.", remoteErrorCode, remoteExceptionType, remoteMessage);
    }

    /**
     * Creates the exception with a custom message.
     *
     * @param message the detail message
     * @param remoteErrorCode the application error code, or null
     * @param remoteExceptionType the remote exception type name, or null
     * @param remoteMessage the remote message, or null
     */
    protected NexusRemoteInvocationException(String message, @Nullable Integer remoteErrorCode,
                                             @Nullable String remoteExceptionType, @Nullable String remoteMessage) {
        super(message);
        this.remoteErrorCode = remoteErrorCode;
        this.remoteExceptionType = remoteExceptionType;
        this.remoteMessage = remoteMessage;
    }

    /**
     * The application error code.
     *
     * @return the code, or null when not sent
     */
    public @Nullable Integer remoteErrorCode() {
        return remoteErrorCode;
    }

    /**
     * The type name of the remote exception.
     *
     * @return the type, or null when not sent
     */
    public @Nullable String remoteExceptionType() {
        return remoteExceptionType;
    }

    /**
     * The message of the remote exception.
     *
     * @return the message, or null when not sent
     */
    public @Nullable String remoteMessage() {
        return remoteMessage;
    }
}
