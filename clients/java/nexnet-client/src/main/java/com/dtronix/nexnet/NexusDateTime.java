package com.dtronix.nexnet;

/**
 * A .NET {@code DateTime}: ticks since 0001-01-01T00:00:00 plus a {@link NexusDateTimeKind}. One tick is 100 ns, as in .NET.
 *
 * <p>The runtime uses this type instead of {@code java.time}, which needs API 26 on Android. Conversions to and from
 * epoch milliseconds and {@code java.time} types are added by the codec work (K02); they must not put
 * {@code java.time} in the signatures the generated code uses.
 */
public final class NexusDateTime {
    private final long ticks;
    private final NexusDateTimeKind kind;

    private NexusDateTime(long ticks, NexusDateTimeKind kind) {
        this.ticks = ticks;
        this.kind = kind;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param ticks the ticks since 0001-01-01T00:00:00
     * @param kind the kind
     * @return the value
     */
    public static NexusDateTime of(long ticks, NexusDateTimeKind kind) {
        if (kind == null) {
            throw new NullPointerException("kind");
        }
        return new NexusDateTime(ticks, kind);
    }

    /**
     * The ticks since 0001-01-01T00:00:00.
     *
     * @return the value
     */
    public long ticks() {
        return ticks;
    }

    /**
     * The kind.
     *
     * @return the value
     */
    public NexusDateTimeKind kind() {
        return kind;
    }

    /**
     * Milliseconds since the Unix epoch, treating the ticks as UTC.
     *
     * @return the epoch milliseconds
     */
    public long toEpochMillis() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Creates a UTC value from milliseconds since the Unix epoch.
     *
     * @param epochMillis the epoch milliseconds
     * @return the value
     */
    public static NexusDateTime fromEpochMillisUtc(long epochMillis) {
        throw new UnsupportedOperationException("not implemented");
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusDateTime)) {
            return false;
        }
        NexusDateTime other = (NexusDateTime) obj;
        return ticks == other.ticks && kind.equals(other.kind);
    }

    @Override
    public int hashCode() {
        return 31 * (Long.hashCode(ticks)) + kind.hashCode();
    }

    @Override
    public String toString() {
        return "NexusDateTime[ticks=" + ticks + ", kind=" + kind + "]";
    }
}
