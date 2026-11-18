package com.dtronix.nexnet;

/**
 * A .NET {@code TimeOnly}: the time of day in ticks since midnight. One tick is 100 ns, as in .NET.
 */
public final class NexusTimeOnly {
    private final long ticks;

    private NexusTimeOnly(long ticks) {
        this.ticks = ticks;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param ticks the ticks since midnight
     * @return the value
     */
    public static NexusTimeOnly of(long ticks) {
        return new NexusTimeOnly(ticks);
    }

    /**
     * The ticks since midnight.
     *
     * @return the value
     */
    public long ticks() {
        return ticks;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusTimeOnly)) {
            return false;
        }
        NexusTimeOnly other = (NexusTimeOnly) obj;
        return ticks == other.ticks;
    }

    @Override
    public int hashCode() {
        return Long.hashCode(ticks);
    }

    @Override
    public String toString() {
        return "NexusTimeOnly[ticks=" + ticks + "]";
    }
}
