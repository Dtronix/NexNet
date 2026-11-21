package com.dtronix.nexnet.metrics;

import java.util.Map;

/**
 * Receives the event instruments of a {@link NexusMetrics} as they happen: counter increments and histogram values.
 * The SPI to bridge NexNet metrics to a metrics library (Micrometer, OpenTelemetry). Instrument names and tags are
 * the C# ones. Called on runtime threads: implementations must be thread-safe and must not block.
 */
public interface NexusMetricsListener {
    /**
     * A {@link InstrumentKind#COUNTER} increment.
     *
     * @param instrument the instrument
     * @param value the increment
     * @param tags the tags (unmodifiable)
     */
    default void add(NexusInstrument instrument, long value, Map<String, String> tags) {
    }

    /**
     * A {@link InstrumentKind#HISTOGRAM} value, in the instrument's unit.
     *
     * @param instrument the instrument
     * @param value the value
     * @param tags the tags (unmodifiable)
     */
    default void record(NexusInstrument instrument, double value, Map<String, String> tags) {
    }
}
