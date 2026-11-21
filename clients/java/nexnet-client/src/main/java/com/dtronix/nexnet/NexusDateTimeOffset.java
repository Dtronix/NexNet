package com.dtronix.nexnet;

/**
 * A .NET {@code DateTimeOffset}: local clock ticks since 0001-01-01T00:00:00 plus the offset from UTC in minutes.
 * One tick is 100 ns, as in .NET. The wire form is {@code [utcTicks, offsetMinutes]} ({@link #utcTicks()},
 * {@link #ofUtc(long, short)}).
 *
 * <p>{@code java.time} (API 26) is not used; the epoch conversions map one to one onto it:
 * {@code OffsetDateTime.ofInstant(Instant.ofEpochSecond(v.toEpochSecond(), v.nanoOfSecond()),
 * ZoneOffset.ofTotalSeconds(v.offsetMinutes() * 60))}.
 */
public final class NexusDateTimeOffset {
    /** The largest offset from UTC, in minutes (14 hours), either way. */
    public static final int MAX_OFFSET_MINUTES = 840;

    /** Ticks per minute. */
    public static final long TICKS_PER_MINUTE = 600_000_000L;

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
     * Creates a value from its wire form: the UTC ticks and the offset.
     *
     * @param utcTicks the UTC ticks since 0001-01-01T00:00:00
     * @param offsetMinutes the offset from UTC in minutes, -840 to 840
     * @return the value
     * @throws IllegalArgumentException when the offset or the UTC or local ticks are outside the .NET range
     */
    public static NexusDateTimeOffset ofUtc(long utcTicks, short offsetMinutes) {
        NexusDateTimeOffset value = new NexusDateTimeOffset(utcTicks + offsetMinutes * TICKS_PER_MINUTE, offsetMinutes);
        if (!value.isValid()) {
            throw new IllegalArgumentException("Not a .NET DateTimeOffset: UTC ticks " + utcTicks + ", offset "
                + offsetMinutes + " minutes.");
        }
        return value;
    }

    /**
     * True when the offset is within ±14 hours and the local and UTC ticks are within the .NET range.
     *
     * @return whether the value is a valid .NET {@code DateTimeOffset}
     */
    public boolean isValid() {
        long utc = ticks - offsetMinutes * TICKS_PER_MINUTE;
        return offsetMinutes >= -MAX_OFFSET_MINUTES && offsetMinutes <= MAX_OFFSET_MINUTES
            && ticks >= 0 && ticks <= NexusDateTime.MAX_TICKS && utc >= 0 && utc <= NexusDateTime.MAX_TICKS;
    }

    /**
     * The UTC ticks since 0001-01-01T00:00:00 (the wire form).
     *
     * @return the UTC ticks
     */
    public long utcTicks() {
        return ticks - offsetMinutes * TICKS_PER_MINUTE;
    }

    /**
     * Milliseconds since the Unix epoch of the instant, rounded toward negative infinity.
     *
     * @return the epoch milliseconds
     */
    public long toEpochMillis() {
        return Math.floorDiv(utcTicks() - NexusDateTime.UNIX_EPOCH_TICKS, NexusDateTime.TICKS_PER_MILLISECOND);
    }

    /**
     * Seconds since the Unix epoch of the instant, rounded toward negative infinity.
     *
     * @return the epoch seconds
     */
    public long toEpochSecond() {
        return Math.floorDiv(utcTicks() - NexusDateTime.UNIX_EPOCH_TICKS, NexusDateTime.TICKS_PER_SECOND);
    }

    /**
     * The nanoseconds within the second of {@link #toEpochSecond()}.
     *
     * @return the nanoseconds, 0 to 999,999,900
     */
    public int nanoOfSecond() {
        return (int) (Math.floorMod(utcTicks() - NexusDateTime.UNIX_EPOCH_TICKS, NexusDateTime.TICKS_PER_SECOND) * 100);
    }

    /**
     * Creates a value from an instant (seconds and nanoseconds since the Unix epoch) and an offset; nanoseconds are
     * truncated to whole ticks.
     *
     * @param epochSecond the epoch seconds
     * @param nanoOfSecond the nanoseconds, 0 to 999,999,999
     * @param offsetMinutes the offset from UTC in minutes, -840 to 840
     * @return the value
     * @throws IllegalArgumentException when the value is outside the .NET range
     */
    public static NexusDateTimeOffset ofEpochSecond(long epochSecond, int nanoOfSecond, short offsetMinutes) {
        NexusDateTime utc = NexusDateTime.ofEpochSecond(epochSecond, nanoOfSecond, NexusDateTimeKind.UTC);
        return ofUtc(utc.ticks(), offsetMinutes);
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
