package com.dtronix.nexnet.invocation;

/**
 * How much of an exception thrown by a client nexus method is sent to the server, like the C#
 * {@code RemoteExceptionDetail}. An application error returned by
 * {@code ClientNexusBase.onInvocationException} is sent whatever this says.
 */
public enum RemoteExceptionDetail {
    /** Nothing: the server sees a failure without detail. The default. */
    NONE,
    /**
     * The exception's class name and message. They can reveal internal details (paths, connection strings), so use
     * this only with a trusted peer.
     */
    TYPE_AND_MESSAGE
}
