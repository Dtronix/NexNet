package com.dtronix.nexnet.metrics;

import java.util.Map;

/**
 * Receives the observable instruments from {@link NexusMetrics#collect}: one call per instrument and tag set.
 */
@FunctionalInterface
public interface MeasurementObserver {
    /**
     * One measurement. Counter values are cumulative totals; up-down counter values are the current level.
     *
     * @param instrument the instrument
     * @param value the value
     * @param tags the tags (unmodifiable)
     */
    void observe(NexusInstrument instrument, long value, Map<String, String> tags);
}
