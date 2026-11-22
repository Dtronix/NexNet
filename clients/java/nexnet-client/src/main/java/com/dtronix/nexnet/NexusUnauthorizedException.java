package com.dtronix.nexnet;

/**
 * The server refused an invocation because the session is not authorized for the method ({@code [NexusAuthorize]}).
 */
public final class NexusUnauthorizedException extends NexusRemoteInvocationException {
    private static final long serialVersionUID = 1L;

    /** Creates the exception. */
    public NexusUnauthorizedException() {
        super("The session is not authorized to invoke this method.", null, null, null);
    }
}
