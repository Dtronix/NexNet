package com.dtronix.nexnet;

/**
 * A .NET {@code DateOnly}: the number of days since 0001-01-01.
 */
public final class NexusDateOnly {
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
