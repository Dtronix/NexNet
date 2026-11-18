package com.dtronix.nexnet;

/**
 * A .NET {@code decimal} in its four-integer form: a 96-bit unsigned magnitude ({@code lo}, {@code mid}, {@code hi})
 * and {@code flags} holding the scale (bits 16–23, 0–28) and the sign (bit 31).
 *
 * <p>Generated code uses {@link java.math.BigDecimal} for C# {@code decimal}; the codec converts through this type and
 * rejects values beyond 96 bits or with a scale above 28.
 */
public final class NexusDecimal {
    private final int lo;
    private final int mid;
    private final int hi;
    private final int flags;

    private NexusDecimal(int lo, int mid, int hi, int flags) {
        this.lo = lo;
        this.mid = mid;
        this.hi = hi;
        this.flags = flags;
    }

    /**
     * Creates a value from its wire representation.
     *
     * @param lo the low 32 bits of the magnitude
     * @param mid the middle 32 bits of the magnitude
     * @param hi the high 32 bits of the magnitude
     * @param flags the scale and sign flags
     * @return the value
     */
    public static NexusDecimal of(int lo, int mid, int hi, int flags) {
        return new NexusDecimal(lo, mid, hi, flags);
    }

    /**
     * The low 32 bits of the magnitude.
     *
     * @return the value
     */
    public int lo() {
        return lo;
    }

    /**
     * The middle 32 bits of the magnitude.
     *
     * @return the value
     */
    public int mid() {
        return mid;
    }

    /**
     * The high 32 bits of the magnitude.
     *
     * @return the value
     */
    public int hi() {
        return hi;
    }

    /**
     * The scale and sign flags.
     *
     * @return the value
     */
    public int flags() {
        return flags;
    }

    /**
     * Converts a {@link java.math.BigDecimal}.
     *
     * @param value the value
     * @return the decimal
     * @throws ArithmeticException when the value needs more than 96 bits or a scale above 28
     */
    public static NexusDecimal fromBigDecimal(java.math.BigDecimal value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Converts to a {@link java.math.BigDecimal}.
     *
     * @return the value
     */
    public java.math.BigDecimal toBigDecimal() {
        throw new UnsupportedOperationException("not implemented");
    }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusDecimal)) {
            return false;
        }
        NexusDecimal other = (NexusDecimal) obj;
        return lo == other.lo && mid == other.mid && hi == other.hi && flags == other.flags;
    }

    @Override
    public int hashCode() {
        return 31 * (31 * (31 * (lo) + mid) + hi) + flags;
    }

    @Override
    public String toString() {
        return "NexusDecimal[lo=" + lo + ", mid=" + mid + ", hi=" + hi + ", flags=" + flags + "]";
    }
}
