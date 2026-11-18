package com.dtronix.nexnet;

/**
 * A .NET {@code TimeSpan}: a signed duration in ticks. One tick is 100 ns, as in .NET.
 */
public final class NexusTimeSpan {
    private final long ticks;

    private NexusTimeSpan(long ticks) {
        this.ticks = ticks;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param ticks the duration in ticks
     * @return the value
     */
    public static NexusTimeSpan of(long ticks) {
        return new NexusTimeSpan(ticks);
    }

    /**
     * The duration in ticks.
     *
     * @return the value
     */
    public long ticks() {
        return ticks;
    }

    /**
     * Creates a value from milliseconds.
     *
     * @param millis the duration in milliseconds
     * @return the value
     * @throws ArithmeticException when the duration does not fit in ticks
     */
    public static NexusTimeSpan ofMillis(long millis) {
        return new NexusTimeSpan(Math.multiplyExact(millis, 10_000L));
    }

    /**
     * The duration in whole milliseconds, truncated toward zero.
     *
     * @return the milliseconds
     */
    public long toMillis() {
        return ticks / 10_000L;
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusTimeSpan)) {
            return false;
        }
        NexusTimeSpan other = (NexusTimeSpan) obj;
        return ticks == other.ticks;
    }

    @Override
    public int hashCode() {
        return Long.hashCode(ticks);
    }

    @Override
    public String toString() {
        return "NexusTimeSpan[ticks=" + ticks + "]";
    }
}
