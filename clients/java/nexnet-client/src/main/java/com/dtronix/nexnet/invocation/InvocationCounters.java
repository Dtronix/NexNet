package com.dtronix.nexnet.invocation;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * Process-wide diagnostic counters of the invocation runtime, for leak audits (H05): each returns to 0 once every
 * client has closed. Read-only for users.
 */
public final class InvocationCounters {
    static final AtomicInteger PENDING_CALLS = new AtomicInteger();
    static final AtomicInteger INBOUND_INVOCATIONS = new AtomicInteger();
    static final AtomicInteger LARGE_MESSAGES = new AtomicInteger();

    private InvocationCounters() {
    }

    /**
     * Calls to the server not yet completed: waiting for credit, being sent or waiting for their result.
     *
     * @return the count
     */
    public static int pendingCalls() {
        return PENDING_CALLS.get();
    }

    /**
     * Invocations of client nexus methods received and not yet finished, queued or running.
     *
     * @return the count
     */
    public static int inboundInvocations() {
        return INBOUND_INVOCATIONS.get();
    }

    /**
     * Large arguments and results being sent or received.
     *
     * @return the count
     */
    public static int largeMessages() {
        return LARGE_MESSAGES.get();
    }
}
