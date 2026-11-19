package com.dtronix.nexnet.reconnect;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * Process-wide diagnostic counters of reconnection, for leak audits (H05). Read-only for users; summed by
 * {@code Diagnostics.counters()}.
 */
public final class ReconnectCounters {
    static final AtomicInteger PENDING_DELAYS = new AtomicInteger();
    static final AtomicInteger RUNNING_LOOPS = new AtomicInteger();

    private ReconnectCounters() {
    }

    /**
     * Reconnection delays scheduled and neither elapsed nor canceled.
     *
     * @return the count
     */
    public static int pendingDelays() {
        return PENDING_DELAYS.get();
    }

    /**
     * Reconnection loops running (waiting or connecting).
     *
     * @return the count
     */
    public static int runningLoops() {
        return RUNNING_LOOPS.get();
    }
}
