package com.dtronix.nexnet;

/**
 * A .NET {@code decimal} in its four-integer form: a 96-bit unsigned magnitude ({@code lo}, {@code mid}, {@code hi})
 * and {@code flags} holding the scale (bits 16–23, 0–28) and the sign (bit 31).
 *
 * <p>Generated code uses {@link java.math.BigDecimal} for C# {@code decimal}; the codec converts through this type and
 * rejects values beyond 96 bits or with a scale above 28.
 */
public final class NexusDecimal {
    /** The largest scale (digits after the decimal point). */
    public static final int MAX_SCALE = 28;

    private static final int SIGN_BIT = 0x8000_0000;
    private static final int SCALE_MASK = 0x00FF_0000;

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
     * Converts a {@link java.math.BigDecimal}, keeping its scale. A negative scale is expanded to 0; trailing zeros are
     * dropped only when the value would not fit otherwise.
     *
     * @param value the value
     * @return the decimal
     * @throws ArithmeticException when the value needs more than 96 bits or a scale above 28
     */
    public static NexusDecimal fromBigDecimal(java.math.BigDecimal value) {
        java.math.BigDecimal d = value;
        if (d.scale() < 0) {
            d = d.setScale(0);
        }
        if (d.scale() > MAX_SCALE || d.unscaledValue().bitLength() > 96) {
            // Trailing zeros after the point can be dropped without changing the value.
            java.math.BigDecimal stripped = d.stripTrailingZeros();
            if (stripped.scale() < 0) {
                stripped = stripped.setScale(0);
            }
            if (stripped.scale() > MAX_SCALE) {
                throw new ArithmeticException("The scale " + d.scale() + " is above " + MAX_SCALE + ": " + value);
            }
            if (stripped.unscaledValue().bitLength() > 96) {
                throw new ArithmeticException("The value needs more than 96 bits: " + value);
            }
            d = stripped;
        }
        java.math.BigInteger unscaled = d.unscaledValue();
        java.math.BigInteger magnitude = unscaled.abs();
        int flags = (d.scale() << 16) | (unscaled.signum() < 0 ? SIGN_BIT : 0);
        return new NexusDecimal(magnitude.intValue(), magnitude.shiftRight(32).intValue(),
            magnitude.shiftRight(64).intValue(), flags);
    }

    /**
     * True when the flags are valid: a scale of 0 to 28 and no bit set other than the scale and the sign.
     *
     * @param flags the flags
     * @return whether a .NET {@code decimal} accepts them
     */
    public static boolean isValidFlags(int flags) {
        return (flags & ~(SIGN_BIT | SCALE_MASK)) == 0 && ((flags & SCALE_MASK) >>> 16) <= MAX_SCALE;
    }

    /**
     * The scale: the number of digits after the decimal point, 0 to 28 for a valid value.
     *
     * @return the scale
     */
    public int scale() {
        return (flags & SCALE_MASK) >>> 16;
    }

    /**
     * True when the sign bit is set (a negative value, or negative zero).
     *
     * @return whether the sign bit is set
     */
    public boolean isNegative() {
        return (flags & SIGN_BIT) != 0;
    }

    /**
     * Converts to a {@link java.math.BigDecimal}, keeping the scale ({@code 1.50} stays {@code 1.50}). A negative zero
     * becomes zero: {@code BigDecimal} has no sign of zero.
     *
     * @return the value
     */
    public java.math.BigDecimal toBigDecimal() {
        java.math.BigInteger magnitude = java.math.BigInteger.valueOf(hi & 0xFFFF_FFFFL).shiftLeft(64)
            .or(java.math.BigInteger.valueOf(mid & 0xFFFF_FFFFL).shiftLeft(32))
            .or(java.math.BigInteger.valueOf(lo & 0xFFFF_FFFFL));
        return new java.math.BigDecimal(isNegative() ? magnitude.negate() : magnitude, scale());
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
