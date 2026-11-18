package com.dtronix.nexnet;

/**
 * A .NET {@code DateTimeOffset}: local clock ticks since 0001-01-01T00:00:00 plus the offset from UTC in minutes.
 * One tick is 100 ns, as in .NET.
 */
public final class NexusDateTimeOffset {
    private final long ticks;
    private final short offsetMinutes;

    private NexusDateTimeOffset(long ticks, short offsetMinutes) {
        this.ticks = ticks;
        this.offsetMinutes = offsetMinutes;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param ticks the local clock ticks since 0001-01-01T00:00:00
     * @param offsetMinutes the offset from UTC in minutes
     * @return the value
     */
    public static NexusDateTimeOffset of(long ticks, short offsetMinutes) {
        return new NexusDateTimeOffset(ticks, offsetMinutes);
    }

    /**
     * The local clock ticks since 0001-01-01T00:00:00.
     *
     * @return the value
     */
    public long ticks() {
        return ticks;
    }

    /**
     * The offset from UTC in minutes.
     *
     * @return the value
     */
    public short offsetMinutes() {
        return offsetMinutes;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusDateTimeOffset)) {
            return false;
        }
        NexusDateTimeOffset other = (NexusDateTimeOffset) obj;
        return ticks == other.ticks && offsetMinutes == other.offsetMinutes;
    }

    @Override
    public int hashCode() {
        return 31 * (Long.hashCode(ticks)) + offsetMinutes;
    }

    @Override
    public String toString() {
        return "NexusDateTimeOffset[ticks=" + ticks + ", offsetMinutes=" + offsetMinutes + "]";
    }
}
