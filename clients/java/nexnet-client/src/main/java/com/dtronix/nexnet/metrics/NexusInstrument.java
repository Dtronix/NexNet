package com.dtronix.nexnet.metrics;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * One instrument of the NexNet meter: name, kind, unit and tags identical to the C# {@code NexusMetrics}
 * (docs/articles/metrics.md). The instances are the constants of {@link NexusInstruments}.
 */
public final class NexusInstrument {
    private final String name;
    private final InstrumentKind kind;
    private final String unit;
    private final String description;
    private final List<String> tags;
    private final boolean serverOnly;
    private final double[] buckets;

    NexusInstrument(String name, InstrumentKind kind, String unit, String description, List<String> tags,
                    boolean serverOnly, double[] buckets) {
        this.name = name;
        this.kind = kind;
        this.unit = unit;
        this.description = description;
        this.tags = Collections.unmodifiableList(tags);
        this.serverOnly = serverOnly;
        this.buckets = buckets.clone();
    }

    /**
     * The instrument name, for example {@code nexnet.session.active}.
     *
     * @return the name
     */
    public String name() {
        return name;
    }

    /**
     * How the instrument reports.
     *
     * @return the kind
     */
    public InstrumentKind kind() {
        return kind;
    }

    /**
     * The UCUM unit, for example {@code By} or {@code {session}}.
     *
     * @return the unit
     */
    public String unit() {
        return unit;
    }

    /**
     * The description.
     *
     * @return the description
     */
    public String description() {
        return description;
    }

    /**
     * The tag keys beyond {@code nexnet.role} and {@code nexnet.transport} (and {@code nexnet.endpoint} on a server).
     *
     * @return the keys
     */
    public List<String> tags() {
        return tags;
    }

    /**
     * Whether only a server records the instrument.
     *
     * @return true for a server-only instrument
     */
    public boolean serverOnly() {
        return serverOnly;
    }

    /**
     * The histogram bucket boundaries C# advises, in the instrument's unit; empty for other kinds.
     *
     * @return a copy of the boundaries
     */
    public double[] buckets() {
        return buckets.clone();
    }

    @Override
    public String toString() {
        return name + " (" + kind + ", " + unit + (tags.isEmpty() ? "" : ", " + tags) + ")"
            + (buckets.length == 0 ? "" : " " + Arrays.toString(buckets));
    }
}
