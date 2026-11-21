package com.dtronix.nexnet.session;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * Process-wide diagnostic counters of the session core, for leak audits (H05): each returns to 0 once every client
 * has closed. Read-only for users.
 */
public final class SessionCounters {
    static final AtomicInteger OPEN_SESSIONS = new AtomicInteger();
    static final AtomicInteger LIVE_THREADS = new AtomicInteger();
    static final AtomicInteger ACTIVE_TIMERS = new AtomicInteger();
    static final AtomicInteger PENDING_WRITES = new AtomicInteger();

    private SessionCounters() {
    }

    /**
     * Sessions started and not yet closed.
     *
     * @return the count
     */
    public static int openSessions() {
        return OPEN_SESSIONS.get();
    }

    /**
     * Reader and writer threads that have not ended.
     *
     * @return the count
     */
    public static int liveThreads() {
        return LIVE_THREADS.get();
    }

    /**
     * Session timers scheduled and neither run nor canceled.
     *
     * @return the count
     */
    public static int activeTimers() {
        return ACTIVE_TIMERS.get();
    }

    /**
     * Frames queued for a writer thread and not yet written or failed.
     *
     * @return the count
     */
    public static int pendingWrites() {
        return PENDING_WRITES.get();
    }
}
