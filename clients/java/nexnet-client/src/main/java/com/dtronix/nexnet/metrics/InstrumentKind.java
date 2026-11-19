package com.dtronix.nexnet.metrics;

/**
 * How an instrument reports, as in {@code System.Diagnostics.Metrics}.
 */
public enum InstrumentKind {
    /** A monotonic total read by {@link NexusMetrics#collect}. */
    OBSERVABLE_COUNTER,
    /** A current level read by {@link NexusMetrics#collect}. */
    OBSERVABLE_UP_DOWN_COUNTER,
    /** Increments pushed to {@link NexusMetricsListener#add} as they happen. */
    COUNTER,
    /** Values pushed to {@link NexusMetricsListener#record} as they happen. */
    HISTOGRAM
}
