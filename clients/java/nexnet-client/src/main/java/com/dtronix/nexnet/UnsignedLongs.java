package com.dtronix.nexnet;

import java.math.BigInteger;

/**
 * Helpers for C# {@code ulong} values, which Java holds as the raw bits of a {@code long}.
 *
 * <p>{@code Long.toUnsignedString}, {@code Long.parseUnsignedLong}, {@code Long.divideUnsigned} and friends need
 * API 26 on Android; use these instead.
 */
public final class UnsignedLongs {
    private UnsignedLongs() {
    }

    /**
     * Compares two values as unsigned.
     *
     * @param a the first value
     * @param b the second value
     * @return negative, zero or positive as {@code a} is less than, equal to or greater than {@code b}
     */
    public static int compare(long a, long b) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Formats a value as an unsigned decimal number.
     *
     * @param value the raw bits
     * @return the decimal text
     */
    public static String toString(long value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Parses an unsigned decimal number.
     *
     * @param text the text, 0 to 18446744073709551615
     * @return the raw bits
     * @throws NumberFormatException when the text is not an unsigned 64-bit number
     */
    public static long parse(String text) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Converts the raw bits to a non-negative {@link BigInteger}.
     *
     * @param value the raw bits
     * @return the value
     */
    public static BigInteger toBigInteger(long value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Unsigned division.
     *
     * @param dividend the dividend bits
     * @param divisor the divisor bits
     * @return the quotient bits
     */
    public static long divide(long dividend, long divisor) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Unsigned remainder.
     *
     * @param dividend the dividend bits
     * @param divisor the divisor bits
     * @return the remainder bits
     */
    public static long remainder(long dividend, long divisor) {
        throw new UnsupportedOperationException("not implemented");
    }
}
