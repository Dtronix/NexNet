package com.dtronix.nexnet;

import java.util.TimeZone;

/**
 * A .NET {@code DateTime}: ticks since 0001-01-01T00:00:00 plus a {@link NexusDateTimeKind}. One tick is 100 ns, as in .NET.
 *
 * <p>The runtime uses this type instead of {@code java.time}, which needs API 26 on Android. The conversions use
 * epoch values that map one to one onto {@code java.time}:
 * {@code Instant.ofEpochSecond(dt.toEpochSecond(), dt.nanoOfSecond())} and
 * {@code NexusDateTime.ofEpochSecond(i.getEpochSecond(), i.getNano(), NexusDateTimeKind.UTC)}.
 *
 * <p><b>Local values.</b> On the wire a Local {@code DateTime} names an instant: its ticks are UTC ticks, and each
 * side shows the instant in its own time zone (protocol specification section 10.6). A {@link NexusDateTimeKind#LOCAL}
 * value therefore holds the UTC ticks of the instant, so it round-trips exactly whatever the time zone;
 * {@link #localTicks(TimeZone)} gives the wall-clock ticks in a zone.
 */
public final class NexusDateTime {
    /** The largest tick count: 9999-12-31T23:59:59.9999999. */
    public static final long MAX_TICKS = 3_155_378_975_999_999_999L;

    /** The ticks of the Unix epoch, 1970-01-01T00:00:00. */
    public static final long UNIX_EPOCH_TICKS = 621_355_968_000_000_000L;

    /** Ticks per millisecond. */
    public static final long TICKS_PER_MILLISECOND = 10_000L;

    /** Ticks per second. */
    public static final long TICKS_PER_SECOND = 10_000_000L;

    private static final long TICKS_MASK = 0x3FFF_FFFF_FFFF_FFFFL;
    private static final long UTC_BIT = 0x4000_0000_0000_0000L;
    private static final long LOCAL_BIT = 0x8000_0000_0000_0000L;

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
     * True when the ticks are within 0 to {@link #MAX_TICKS}, the .NET range.
     *
     * @return whether the value is a valid .NET {@code DateTime}
     */
    public boolean isValid() {
        return ticks >= 0 && ticks <= MAX_TICKS;
    }

    /**
     * The .NET {@code ToBinary} form, the wire form: bits 0-61 the ticks, bits 62-63 the kind (01 UTC, 10 Local).
     *
     * @return the binary form
     * @throws IllegalArgumentException when the ticks are outside the .NET range
     */
    public long toBinary() {
        if (!isValid()) {
            throw new IllegalArgumentException("ticks outside 0.." + MAX_TICKS + ": " + ticks);
        }
        switch (kind) {
            case UTC:
                return ticks | UTC_BIT;
            case LOCAL:
                return ticks | LOCAL_BIT;
            default:
                return ticks;
        }
    }

    /**
     * Creates a value from the .NET {@code ToBinary} form. Kind bits 11 (.NET's ambiguous-DST flag) read as Local.
     *
     * @param binary the binary form
     * @return the value
     * @throws IllegalArgumentException when the ticks are outside the .NET range
     */
    public static NexusDateTime fromBinary(long binary) {
        long ticks = binary & TICKS_MASK;
        if (ticks > MAX_TICKS) {
            throw new IllegalArgumentException("ticks outside 0.." + MAX_TICKS + ": " + ticks);
        }
        int kindBits = (int) (binary >>> 62);
        NexusDateTimeKind kind = kindBits == 0 ? NexusDateTimeKind.UNSPECIFIED
            : kindBits == 1 ? NexusDateTimeKind.UTC : NexusDateTimeKind.LOCAL;
        return new NexusDateTime(ticks, kind);
    }

    /**
     * Milliseconds since the Unix epoch, treating the ticks as UTC (for a Local value they are), rounded toward
     * negative infinity.
     *
     * @return the epoch milliseconds
     */
    public long toEpochMillis() {
        return Math.floorDiv(ticks - UNIX_EPOCH_TICKS, TICKS_PER_MILLISECOND);
    }

    /**
     * Creates a UTC value from milliseconds since the Unix epoch.
     *
     * @param epochMillis the epoch milliseconds
     * @return the value
     * @throws IllegalArgumentException when the instant is outside the .NET range
     */
    public static NexusDateTime fromEpochMillisUtc(long epochMillis) {
        return fromEpochMillis(epochMillis, NexusDateTimeKind.UTC);
    }

    /**
     * Creates a value from milliseconds since the Unix epoch.
     *
     * @param epochMillis the epoch milliseconds
     * @param kind the kind; for {@link NexusDateTimeKind#LOCAL} the instant is kept (see the class description)
     * @return the value
     * @throws IllegalArgumentException when the instant is outside the .NET range
     */
    public static NexusDateTime fromEpochMillis(long epochMillis, NexusDateTimeKind kind) {
        long ticks;
        try {
            ticks = Math.addExact(Math.multiplyExact(epochMillis, TICKS_PER_MILLISECOND), UNIX_EPOCH_TICKS);
        } catch (ArithmeticException e) {
            throw new IllegalArgumentException("epoch milliseconds outside the .NET range: " + epochMillis, e);
        }
        return ofChecked(ticks, kind);
    }

    private static NexusDateTime ofChecked(long ticks, NexusDateTimeKind kind) {
        if (ticks < 0 || ticks > MAX_TICKS) {
            throw new IllegalArgumentException("ticks outside 0.." + MAX_TICKS + ": " + ticks);
        }
        return of(ticks, kind);
    }

    /**
     * Seconds since the Unix epoch, rounded toward negative infinity (with {@link #nanoOfSecond()}: the
     * {@code java.time.Instant} of the value).
     *
     * @return the epoch seconds
     */
    public long toEpochSecond() {
        return Math.floorDiv(ticks - UNIX_EPOCH_TICKS, TICKS_PER_SECOND);
    }

    /**
     * The nanoseconds within the second of {@link #toEpochSecond()}, 0 to 999,999,900.
     *
     * @return the nanoseconds
     */
    public int nanoOfSecond() {
        return (int) (Math.floorMod(ticks - UNIX_EPOCH_TICKS, TICKS_PER_SECOND) * 100);
    }

    /**
     * Creates a value from seconds and nanoseconds since the Unix epoch (a {@code java.time.Instant}); nanoseconds
     * are truncated to whole ticks.
     *
     * @param epochSecond the epoch seconds
     * @param nanoOfSecond the nanoseconds, 0 to 999,999,999
     * @param kind the kind
     * @return the value
     * @throws IllegalArgumentException when the instant is outside the .NET range
     */
    public static NexusDateTime ofEpochSecond(long epochSecond, int nanoOfSecond, NexusDateTimeKind kind) {
        if (nanoOfSecond < 0 || nanoOfSecond > 999_999_999) {
            throw new IllegalArgumentException("nanoOfSecond: " + nanoOfSecond);
        }
        long ticks;
        try {
            ticks = Math.addExact(Math.addExact(Math.multiplyExact(epochSecond, TICKS_PER_SECOND), nanoOfSecond / 100),
                UNIX_EPOCH_TICKS);
        } catch (ArithmeticException e) {
            throw new IllegalArgumentException("epoch seconds outside the .NET range: " + epochSecond, e);
        }
        return ofChecked(ticks, kind);
    }

    /**
     * The wall-clock ticks of the value in a time zone: for a UTC or Local value the instant shown in {@code zone};
     * an Unspecified value is returned as it is.
     *
     * @param zone the time zone, for example {@code TimeZone.getDefault()}
     * @return the local ticks
     */
    public long localTicks(TimeZone zone) {
        if (kind == NexusDateTimeKind.UNSPECIFIED) {
            return ticks;
        }
        return ticks + zone.getOffset(toEpochMillis()) * TICKS_PER_MILLISECOND;
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
