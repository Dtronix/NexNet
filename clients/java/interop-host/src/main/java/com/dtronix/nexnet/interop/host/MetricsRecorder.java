package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.metrics.NexusInstrument;
import com.dtronix.nexnet.metrics.NexusMetrics;
import com.dtronix.nexnet.metrics.NexusMetricsListener;
import com.dtronix.nexnet.micrometer.NexusMicrometerBinder;
import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.DistributionSummary;
import io.micrometer.core.instrument.FunctionCounter;
import io.micrometer.core.instrument.Gauge;
import io.micrometer.core.instrument.Meter;
import io.micrometer.core.instrument.Tag;
import io.micrometer.core.instrument.simple.SimpleMeterRegistry;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * The metrics of one client ({@code ClientSpec.metrics}, control protocol §6.4 {@code client.metrics}).
 *
 * <ul>
 *   <li>{@code Native}: a {@link NexusMetrics} tracking the client, with a listener that keeps every event
 *       measurement; the observable instruments are collected on each read.</li>
 *   <li>{@code Micrometer}: the same metrics reported through {@link NexusMicrometerBinder} to a
 *       {@link SimpleMeterRegistry}, and read back from the registry: counters and distribution summaries as
 *       aggregated events (a counter's total, a summary's sum, each with the number of recordings as {@code count}),
 *       function counters and gauges as observed values.</li>
 * </ul>
 */
final class MetricsRecorder implements AutoCloseable {
    private final NexusMetrics metrics = new NexusMetrics();
    private final List<Map<String, Object>> events = new ArrayList<>();
    private final Registration tracking;
    private final Registration listener;
    private final NexusMicrometerBinder binder;
    private final SimpleMeterRegistry registry;

    private MetricsRecorder(NexusClient<?> client, boolean micrometer) {
        if (micrometer) {
            registry = new SimpleMeterRegistry();
            binder = new NexusMicrometerBinder(metrics, 0);
            binder.bindTo(registry);
            listener = null;
            tracking = binder.track(client);
        } else {
            registry = null;
            binder = null;
            listener = metrics.addListener(new NexusMetricsListener() {
                @Override
                public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
                    event(instrument, value, tags);
                }

                @Override
                public void record(NexusInstrument instrument, double value, Map<String, String> tags) {
                    event(instrument, value, tags);
                }
            });
            tracking = metrics.track(client);
        }
    }

    /**
     * The recorder of a spec's {@code metrics} member, or null when it has none.
     *
     * @param mode {@code Native}, {@code Micrometer} or null
     * @param client the client, tracked from now on
     * @return the recorder, or null
     */
    static MetricsRecorder create(String mode, NexusClient<?> client) {
        if (mode == null) {
            return null;
        }
        return new MetricsRecorder(client, mode.equals("Micrometer"));
    }

    /** Checks a spec's {@code metrics} member. */
    static void validate(String mode) {
        if (mode != null && !mode.equals("Native") && !mode.equals("Micrometer")) {
            throw HostError.badRequest("Unknown MetricsRecording '" + mode + "'.");
        }
    }

    private void event(NexusInstrument instrument, Number value, Map<String, String> tags) {
        Map<String, Object> measurement = Protocol.map("instrument", instrument.name(), "value", value,
            "tags", new LinkedHashMap<String, Object>(tags));
        synchronized (events) {
            events.add(measurement);
        }
    }

    /**
     * The result of {@code client.metrics}.
     *
     * @return {@code {events, observed}}
     */
    Map<String, Object> read() {
        List<Object> observed = new ArrayList<>();
        if (registry != null) {
            binder.refresh();
            List<Object> aggregated = new ArrayList<>();
            for (Meter meter : registry.getMeters()) {
                Map<String, Object> tags = new LinkedHashMap<>();
                for (Tag tag : meter.getId().getTags()) {
                    tags.put(tag.getKey(), tag.getValue());
                }
                String name = meter.getId().getName();
                if (meter instanceof Counter) {
                    double count = ((Counter) meter).count();
                    aggregated.add(Protocol.map("instrument", name, "value", count, "tags", tags, "count", (long) count));
                } else if (meter instanceof DistributionSummary) {
                    DistributionSummary summary = (DistributionSummary) meter;
                    aggregated.add(Protocol.map("instrument", name, "value", summary.totalAmount(), "tags", tags,
                        "count", summary.count()));
                } else if (meter instanceof FunctionCounter) {
                    observed.add(Protocol.map("instrument", name, "value", ((FunctionCounter) meter).count(), "tags", tags));
                } else if (meter instanceof Gauge) {
                    observed.add(Protocol.map("instrument", name, "value", ((Gauge) meter).value(), "tags", tags));
                }
            }
            return Protocol.map("events", aggregated, "observed", observed);
        }
        metrics.collect((instrument, value, tags) -> observed.add(Protocol.map("instrument", instrument.name(),
            "value", value, "tags", new LinkedHashMap<String, Object>(tags))));
        List<Object> copy;
        synchronized (events) {
            copy = new ArrayList<>(events);
        }
        return Protocol.map("events", copy, "observed", observed);
    }

    @Override
    public void close() {
        tracking.close();
        if (listener != null) {
            listener.close();
        }
        if (binder != null) {
            binder.close();
            registry.close();
        }
        metrics.close();
    }
}
