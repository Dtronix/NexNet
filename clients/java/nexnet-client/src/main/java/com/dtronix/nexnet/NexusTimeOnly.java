package com.dtronix.nexnet;

/**
 * A .NET {@code TimeOnly}: the time of day in ticks since midnight. One tick is 100 ns, as in .NET.
 */
public final class NexusTimeOnly {
    /** The largest tick count: 23:59:59.9999999. */
    public static final long MAX_TICKS = 863_999_999_999L;

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

    /**
     * True when the ticks are within 0 to {@link #MAX_TICKS}.
     *
     * @return whether the value is a valid .NET {@code TimeOnly}
     */
    public boolean isValid() {
        return ticks >= 0 && ticks <= MAX_TICKS;
    }

    /**
     * Nanoseconds since midnight ({@code java.time.LocalTime.ofNanoOfDay(value.toNanoOfDay())}).
     *
     * @return the nanoseconds
     */
    public long toNanoOfDay() {
        return ticks * 100;
    }

    /**
     * Creates a value from nanoseconds since midnight ({@code LocalTime.toNanoOfDay()}), truncated to whole ticks.
     *
     * @param nanoOfDay the nanoseconds, 0 to 86,399,999,999,999
     * @return the value
     * @throws IllegalArgumentException when the time is outside a day
     */
    public static NexusTimeOnly ofNanoOfDay(long nanoOfDay) {
        long ticks = nanoOfDay / 100;
        if (nanoOfDay < 0 || ticks > MAX_TICKS) {
            throw new IllegalArgumentException("nanoOfDay outside a day: " + nanoOfDay);
        }
        return new NexusTimeOnly(ticks);
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
