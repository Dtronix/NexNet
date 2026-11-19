package com.dtronix.nexnet.metrics;

import java.util.concurrent.atomic.AtomicInteger;

/**
 * Process-wide diagnostic counters of metrics, for leak audits (H05). Read-only for users; summed by
 * {@code Diagnostics.counters()}.
 */
public final class MetricsCounters {
    static final AtomicInteger SUBSCRIPTIONS = new AtomicInteger();

    private MetricsCounters() {
    }

    /**
     * Listeners that metrics hold on clients and sessions (one state listener per tracked client).
     *
     * @return the count
     */
    public static int subscriptions() {
        return SUBSCRIPTIONS.get();
    }
}
