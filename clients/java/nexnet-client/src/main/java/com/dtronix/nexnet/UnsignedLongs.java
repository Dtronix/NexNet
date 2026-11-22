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
        return Long.compare(a ^ Long.MIN_VALUE, b ^ Long.MIN_VALUE);
    }

    /**
     * Formats a value as an unsigned decimal number.
     *
     * @param value the raw bits
     * @return the decimal text
     */
    public static String toString(long value) {
        if (value >= 0) {
            return Long.toString(value);
        }
        long quotient = (value >>> 1) / 5;
        long remainder = value - quotient * 10;
        return Long.toString(quotient) + remainder;
    }

    /**
     * Parses an unsigned decimal number.
     *
     * @param text the text, 0 to 18446744073709551615
     * @return the raw bits
     * @throws NumberFormatException when the text is not an unsigned 64-bit number
     */
    public static long parse(String text) {
        int n = text.length();
        if (n == 0 || n > 20) {
            throw new NumberFormatException("Not an unsigned 64-bit number: \"" + text + "\"");
        }
        long result = 0;
        for (int i = 0; i < n; i++) {
            int digit = text.charAt(i) - '0';
            if (digit < 0 || digit > 9) {
                throw new NumberFormatException("Not an unsigned 64-bit number: \"" + text + "\"");
            }
            // result * 10 + digit must not pass 2^64 - 1: compare against (2^64 - 1 - digit) / 10.
            if (i >= 19 && compare(result, divide(-1L - digit, 10)) > 0) {
                throw new NumberFormatException("Out of the unsigned 64-bit range: \"" + text + "\"");
            }
            result = result * 10 + digit;
        }
        return result;
    }

    /**
     * Converts the raw bits to a non-negative {@link BigInteger}.
     *
     * @param value the raw bits
     * @return the value
     */
    public static BigInteger toBigInteger(long value) {
        BigInteger result = BigInteger.valueOf(value);
        return value >= 0 ? result : result.add(BigInteger.ONE.shiftLeft(64));
    }

    /**
     * Converts a value of 0 to 2^64 - 1 to its raw bits.
     *
     * @param value the value
     * @return the raw bits
     * @throws ArithmeticException when the value is negative or needs more than 64 bits
     */
    public static long fromBigInteger(BigInteger value) {
        if (value.signum() < 0 || value.bitLength() > 64) {
            throw new ArithmeticException("Out of the unsigned 64-bit range: " + value);
        }
        return value.longValue();
    }

    /**
     * Unsigned division.
     *
     * @param dividend the dividend bits
     * @param divisor the divisor bits
     * @return the quotient bits
     */
    public static long divide(long dividend, long divisor) {
        if (divisor < 0) {
            // The divisor is at least 2^63: the quotient is 0 or 1.
            return compare(dividend, divisor) < 0 ? 0 : 1;
        }
        if (dividend >= 0) {
            return dividend / divisor;
        }
        long quotient = ((dividend >>> 1) / divisor) << 1;
        long remainder = dividend - quotient * divisor;
        return quotient + (compare(remainder, divisor) >= 0 ? 1 : 0);
    }

    /**
     * Unsigned remainder.
     *
     * @param dividend the dividend bits
     * @param divisor the divisor bits
     * @return the remainder bits
     */
    public static long remainder(long dividend, long divisor) {
        return dividend - divide(dividend, divisor) * divisor;
    }
}
