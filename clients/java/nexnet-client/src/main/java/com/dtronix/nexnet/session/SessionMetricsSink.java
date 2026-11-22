package com.dtronix.nexnet.session;

import com.dtronix.nexnet.metrics.NexusInstrument;
import java.util.Map;

/**
 * Receives the event instruments (counters and histograms) of one session, attached with
 * {@link NexusSession#addMetricsSink}. The session, invocation and pipe layers report through
 * {@link NexusSession#recordMetric}. Runtime-internal.
 */
@FunctionalInterface
public interface SessionMetricsSink {
    /**
     * One measurement. Called synchronously where the event happens, which may be the reader thread: must be cheap and
     * must not block. A throw is ignored.
     *
     * @param instrument the instrument ({@code NexusInstruments})
     * @param value the counter increment, or the histogram value (seconds for durations)
     * @param tags the tags beyond the session's role and transport; empty when there are none
     */
    void record(NexusInstrument instrument, double value, Map<String, String> tags);
}
