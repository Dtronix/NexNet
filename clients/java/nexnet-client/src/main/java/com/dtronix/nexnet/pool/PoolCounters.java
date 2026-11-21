package com.dtronix.nexnet.pool;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * Process-wide diagnostic counters of client pools, for leak audits (H05): each returns to 0 once every pool is
 * closed. Read-only for users; summed by {@code Diagnostics.counters()}.
 */
public final class PoolCounters {
    static final AtomicInteger POOLED_CLIENTS = new AtomicInteger();
    static final AtomicInteger HEALTH_CHECKS = new AtomicInteger();
    static final AtomicInteger PENDING_RENTS = new AtomicInteger();

    private PoolCounters() {
    }

    /**
     * Clients owned by an open pool, rented or idle, and not yet closed.
     *
     * @return the count
     */
    public static int pooledClients() {
        return POOLED_CLIENTS.get();
    }

    /**
     * Health-check timers scheduled: one per open pool.
     *
     * @return the count
     */
    public static int healthChecks() {
        return HEALTH_CHECKS.get();
    }

    /**
     * Rent requests waiting for a free slot.
     *
     * @return the count
     */
    public static int pendingRents() {
        return PENDING_RENTS.get();
    }
}
