package com.dtronix.nexnet;

/**
 * A .NET {@code DateOnly}: the number of days since 0001-01-01.
 */
public final class NexusDateOnly {
    /** The largest day number: 9999-12-31. */
    public static final int MAX_DAY_NUMBER = 3_652_058;

    /** The day number of 1970-01-01, the Unix epoch. */
    public static final int UNIX_EPOCH_DAY_NUMBER = 719_162;

    private final int dayNumber;

    private NexusDateOnly(int dayNumber) {
        this.dayNumber = dayNumber;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param dayNumber the number of days since 0001-01-01
     * @return the value
     */
    public static NexusDateOnly of(int dayNumber) {
        return new NexusDateOnly(dayNumber);
    }

    /**
     * The number of days since 0001-01-01.
     *
     * @return the value
     */
    public int dayNumber() {
        return dayNumber;
    }

    /**
     * True when the day number is within 0 to {@link #MAX_DAY_NUMBER}, the .NET range.
     *
     * @return whether the value is a valid .NET {@code DateOnly}
     */
    public boolean isValid() {
        return dayNumber >= 0 && dayNumber <= MAX_DAY_NUMBER;
    }

    /**
     * Days since 1970-01-01 ({@code java.time.LocalDate.ofEpochDay(value.toEpochDay())}).
     *
     * @return the epoch day
     */
    public long toEpochDay() {
        return (long) dayNumber - UNIX_EPOCH_DAY_NUMBER;
    }

    /**
     * Creates a value from days since 1970-01-01 ({@code LocalDate.toEpochDay()}).
     *
     * @param epochDay the epoch day
     * @return the value
     * @throws IllegalArgumentException when the date is outside the .NET range
     */
    public static NexusDateOnly ofEpochDay(long epochDay) {
        long day = epochDay + UNIX_EPOCH_DAY_NUMBER;
        if (day < 0 || day > MAX_DAY_NUMBER) {
            throw new IllegalArgumentException("epoch day outside the .NET range: " + epochDay);
        }
        return new NexusDateOnly((int) day);
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusDateOnly)) {
            return false;
        }
        NexusDateOnly other = (NexusDateOnly) obj;
        return dayNumber == other.dayNumber;
    }

    @Override
    public int hashCode() {
        return dayNumber;
    }

    @Override
    public String toString() {
        return "NexusDateOnly[dayNumber=" + dayNumber + "]";
    }
}
