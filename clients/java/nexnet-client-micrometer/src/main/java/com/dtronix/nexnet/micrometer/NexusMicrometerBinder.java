package com.dtronix.nexnet.micrometer;

import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.metrics.InstrumentKind;
import com.dtronix.nexnet.metrics.NexusInstrument;
import com.dtronix.nexnet.metrics.NexusMetrics;
import com.dtronix.nexnet.metrics.NexusMetricsListener;
import io.micrometer.core.instrument.Counter;
import io.micrometer.core.instrument.DistributionSummary;
import io.micrometer.core.instrument.FunctionCounter;
import io.micrometer.core.instrument.Gauge;
import io.micrometer.core.instrument.Meter;
import io.micrometer.core.instrument.MeterRegistry;
import io.micrometer.core.instrument.Tag;
import io.micrometer.core.instrument.Tags;
import io.micrometer.core.instrument.binder.MeterBinder;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import org.jspecify.annotations.Nullable;

/**
 * Reports a {@link NexusMetrics} to Micrometer, with the instrument names, descriptions and tags of the C# meter
 * (docs/articles/metrics.md):
 *
 * <ul>
 *   <li>counters ({@code nexnet.session.disconnects}, {@code nexnet.pipe.resets}, ...) become {@link Counter}s;</li>
 *   <li>histograms ({@code nexnet.session.duration}, {@code nexnet.invocation.queue_wait}, ...) become
 *       {@link DistributionSummary}s with the bucket boundaries C# advises as service level objectives;</li>
 *   <li>observable counters ({@code nexnet.io.frames}, {@code nexnet.io.bytes}) become {@link FunctionCounter}s;</li>
 *   <li>observable up-down counters ({@code nexnet.session.active}, {@code nexnet.pipe.open}, ...) become
 *       {@link Gauge}s.</li>
 * </ul>
 *
 * <p>Units map to Micrometer base units: {@code By} to {@code bytes}, {@code s} to {@code seconds}, and an annotation
 * such as {@code {session}} to {@code session}.
 *
 * <pre>{@code
 * NexusMetrics metrics = new NexusMetrics();
 * NexusMicrometerBinder binder = new NexusMicrometerBinder(metrics);
 * binder.bindTo(registry);
 * binder.track(client);   // or metrics.track(client)
 * }</pre>
 *
 * <p>The observable meters read {@link NexusMetrics#collect} when the registry reads them, at most once per
 * collection interval, so a scrape costs one collection. Their tag sets come from the tracked clients: a client
 * tracked through {@link #track} gets its meters at once; a client tracked on the {@link NexusMetrics} directly gets
 * them at the next collection (any observable meter read, or {@link #refresh()}). Thread-safe.
 */
public final class NexusMicrometerBinder implements MeterBinder, AutoCloseable {
    /** Default {@link #collectIntervalMillis()}: one second. */
    public static final long DEFAULT_COLLECT_INTERVAL_MILLIS = 1_000;

    private final NexusMetrics metrics;
    private final long collectIntervalMillis;
    private final CopyOnWriteArrayList<MeterRegistry> registries = new CopyOnWriteArrayList<>();
    private final Object lock = new Object();
    /** The observable values, by instrument and tag set. */
    private final Map<Key, Value> values = new LinkedHashMap<>();
    /** The observable meters registered in each registry. */
    private final List<Meter> observableMeters = new ArrayList<>();
    private final Listener listener = new Listener();
    private @Nullable Registration listenerRegistration;
    private long lastCollectNanos;
    private boolean collected;
    private boolean closed;

    /**
     * Creates a binder that collects at most once per {@value #DEFAULT_COLLECT_INTERVAL_MILLIS} ms.
     *
     * @param metrics the metrics to report
     */
    public NexusMicrometerBinder(NexusMetrics metrics) {
        this(metrics, DEFAULT_COLLECT_INTERVAL_MILLIS);
    }

    /**
     * Creates a binder.
     *
     * @param metrics the metrics to report
     * @param collectIntervalMillis how long the observable values read from {@link NexusMetrics#collect} are reused,
     *     in milliseconds; 0 collects on every read
     */
    public NexusMicrometerBinder(NexusMetrics metrics, long collectIntervalMillis) {
        if (metrics == null) {
            throw new NullPointerException("metrics");
        }
        if (collectIntervalMillis < 0) {
            throw new IllegalArgumentException("collectIntervalMillis must not be negative: " + collectIntervalMillis);
        }
        this.metrics = metrics;
        this.collectIntervalMillis = collectIntervalMillis;
    }

    /**
     * The metrics this binder reports.
     *
     * @return the metrics
     */
    public NexusMetrics metrics() {
        return metrics;
    }

    /**
     * How long collected observable values are reused.
     *
     * @return milliseconds
     */
    public long collectIntervalMillis() {
        return collectIntervalMillis;
    }

    /**
     * Registers the NexNet meters in a registry. A binder may be bound to several registries; binding the same
     * registry again does nothing.
     *
     * @param registry the registry
     * @throws IllegalStateException when the binder is closed
     */
    @Override
    public void bindTo(MeterRegistry registry) {
        if (registry == null) {
            throw new NullPointerException("registry");
        }
        synchronized (lock) {
            if (closed) {
                throw new IllegalStateException("The binder is closed.");
            }
            if (registries.contains(registry)) {
                return;
            }
            registries.add(registry);
            if (listenerRegistration == null) {
                listenerRegistration = metrics.addListener(listener);
            }
            for (Map.Entry<Key, Value> entry : values.entrySet()) {
                register(registry, entry.getKey(), entry.getValue());
            }
        }
        refresh();
    }

    /**
     * Tracks a client on the metrics ({@link NexusMetrics#track}) and registers its observable meters at once.
     *
     * @param client the client
     * @return a registration that stops tracking it
     */
    public Registration track(NexusClient<?> client) {
        Registration registration = metrics.track(client);
        refresh();
        return registration;
    }

    /**
     * Collects the observable values now and registers the meters of tag sets not seen before.
     */
    public void refresh() {
        synchronized (lock) {
            if (!closed) {
                collectLocked();
            }
        }
    }

    /** Removes the listener and every meter this binder registered. Closing again does nothing. */
    @Override
    public void close() {
        List<Meter> meters;
        Registration registration;
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            registration = listenerRegistration;
            listenerRegistration = null;
            meters = new ArrayList<>(observableMeters);
            observableMeters.clear();
            values.clear();
        }
        if (registration != null) {
            registration.close();
        }
        for (MeterRegistry registry : registries) {
            for (Meter meter : meters) {
                registry.remove(meter);
            }
            for (Meter.Id id : listener.registered) {
                registry.remove(id);
            }
        }
        registries.clear();
    }

    /** The value of an observable meter: collects first when the values are older than the interval. */
    private double read(Value value) {
        synchronized (lock) {
            if (!closed && (!collected || System.nanoTime() - lastCollectNanos >= collectIntervalMillis * 1_000_000L)) {
                collectLocked();
            }
            return value.current;
        }
    }

    private void collectLocked() {
        final Set<Key> seen = new HashSet<>();
        metrics.collect((instrument, measured, tags) -> {
            Key key = new Key(instrument, tags);
            seen.add(key);
            Value value = values.get(key);
            if (value == null) {
                value = new Value();
                values.put(key, value);
                for (MeterRegistry registry : registries) {
                    register(registry, key, value);
                }
            }
            value.current = measured;
        });
        // A level whose clients are gone is 0; a total keeps its last value (the metrics keep ended sessions' totals).
        for (Map.Entry<Key, Value> entry : values.entrySet()) {
            if (!seen.contains(entry.getKey())
                && entry.getKey().instrument.kind() == InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER) {
                entry.getValue().current = 0;
            }
        }
        lastCollectNanos = System.nanoTime();
        collected = true;
    }

    private void register(MeterRegistry registry, Key key, Value value) {
        NexusInstrument instrument = key.instrument;
        Meter meter;
        if (instrument.kind() == InstrumentKind.OBSERVABLE_COUNTER) {
            meter = FunctionCounter.builder(instrument.name(), value, this::read)
                .description(instrument.description())
                .baseUnit(baseUnit(instrument.unit()))
                .tags(key.tags)
                .register(registry);
        } else {
            meter = Gauge.builder(instrument.name(), value, this::read)
                .description(instrument.description())
                .baseUnit(baseUnit(instrument.unit()))
                .tags(key.tags)
                .strongReference(true)
                .register(registry);
        }
        observableMeters.add(meter);
    }

    /**
     * The Micrometer base unit of a UCUM unit: {@code By} is {@code bytes}, {@code s} is {@code seconds}, and an
     * annotation such as {@code {session}} loses its braces.
     *
     * @param unit the unit of a {@link NexusInstrument}
     * @return the base unit
     */
    public static String baseUnit(String unit) {
        switch (unit) {
            case "By":
                return "bytes";
            case "s":
                return "seconds";
            default:
                if (unit.length() > 2 && unit.charAt(0) == '{' && unit.charAt(unit.length() - 1) == '}') {
                    return unit.substring(1, unit.length() - 1);
                }
                return unit;
        }
    }

    static Tags tags(Map<String, String> tags) {
        List<Tag> list = new ArrayList<>(tags.size());
        for (Map.Entry<String, String> tag : tags.entrySet()) {
            list.add(Tag.of(tag.getKey(), tag.getValue()));
        }
        return Tags.of(list);
    }

    /** Receives the counters and histograms as they happen. */
    private final class Listener implements NexusMetricsListener {
        /** The ids of the counters and summaries registered, removed on close. */
        final Set<Meter.Id> registered = Collections.newSetFromMap(new ConcurrentHashMap<Meter.Id, Boolean>());

        @Override
        public void add(NexusInstrument instrument, long value, Map<String, String> tags) {
            Tags micrometerTags = tags(tags);
            for (MeterRegistry registry : registries) {
                Counter counter = Counter.builder(instrument.name())
                    .description(instrument.description())
                    .baseUnit(baseUnit(instrument.unit()))
                    .tags(micrometerTags)
                    .register(registry);
                registered.add(counter.getId());
                counter.increment(value);
            }
        }

        @Override
        public void record(NexusInstrument instrument, double value, Map<String, String> tags) {
            Tags micrometerTags = tags(tags);
            for (MeterRegistry registry : registries) {
                DistributionSummary summary = DistributionSummary.builder(instrument.name())
                    .description(instrument.description())
                    .baseUnit(baseUnit(instrument.unit()))
                    .serviceLevelObjectives(instrument.buckets())
                    .tags(micrometerTags)
                    .register(registry);
                registered.add(summary.getId());
                summary.record(value);
            }
        }
    }

    /** An observable instrument and tag set. */
    private static final class Key {
        final NexusInstrument instrument;
        final Tags tags;
        private final Map<String, String> rawTags;

        Key(NexusInstrument instrument, Map<String, String> tags) {
            this.instrument = instrument;
            this.rawTags = tags;
            this.tags = tags(tags);
        }

        @Override
        public boolean equals(@Nullable Object obj) {
            if (!(obj instanceof Key)) {
                return false;
            }
            Key other = (Key) obj;
            return instrument == other.instrument && rawTags.equals(other.rawTags);
        }

        @Override
        public int hashCode() {
            return instrument.hashCode() * 31 + rawTags.hashCode();
        }
    }

    /** The last collected value of an observable meter. */
    private static final class Value {
        double current;
    }
}
