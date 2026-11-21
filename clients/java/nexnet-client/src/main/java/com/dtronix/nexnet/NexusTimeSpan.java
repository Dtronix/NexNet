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
     * Creates a value from seconds and nanoseconds (a {@code java.time.Duration}); nanoseconds are truncated to whole
     * ticks.
     *
     * @param seconds the seconds
     * @param nanoAdjustment the nanoseconds to add, 0 to 999,999,999
     * @return the value
     * @throws ArithmeticException when the duration does not fit in ticks
     */
    public static NexusTimeSpan ofSeconds(long seconds, long nanoAdjustment) {
        return new NexusTimeSpan(Math.addExact(Math.multiplyExact(seconds, 10_000_000L), nanoAdjustment / 100));
    }

    /**
     * The whole seconds, rounded toward negative infinity (with {@link #nanoAdjustment()}: the
     * {@code java.time.Duration} of the value).
     *
     * @return the seconds
     */
    public long toSeconds() {
        return Math.floorDiv(ticks, 10_000_000L);
    }

    /**
     * The nanoseconds within the second of {@link #toSeconds()}.
     *
     * @return the nanoseconds, 0 to 999,999,900
     */
    public int nanoAdjustment() {
        return (int) (Math.floorMod(ticks, 10_000_000L) * 100);
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
