package com.dtronix.nexnet.serialization;

/**
 * IEEE 754 binary16 (C# {@code Half}) conversions, with the results of .NET's {@code (Half)float} and
 * {@code (float)Half}: rounding to nearest, ties to even; overflow to infinity; NaN sign and payload kept (made
 * quiet). Java 11 and Android have no float16 type, so C# {@code Half} maps to {@code float}.
 */
public final class Float16 {
    private Float16() {
    }

    /**
     * Converts binary16 bits to the {@code float} of the same value (exact).
     *
     * @param bits the binary16 bits
     * @return the value
     */
    public static float toFloat(short bits) {
        int h = bits & 0xFFFF;
        int sign = (h & 0x8000) << 16;
        int exp = (h >>> 10) & 0x1F;
        int mant = h & 0x3FF;
        if (exp == 0) {
            float v = mant * 0x1p-24f; // subnormal or zero: exact
            return sign != 0 ? -v : v;
        }
        if (exp == 31) {
            if (mant == 0) {
                return Float.intBitsToFloat(sign | 0x7F80_0000);
            }
            return Float.intBitsToFloat(sign | 0x7FC0_0000 | (mant << 13));
        }
        return Float.intBitsToFloat(sign | ((exp - 15 + 127) << 23) | (mant << 13));
    }

    /**
     * Converts a {@code float} to the nearest binary16 value (ties to even).
     *
     * @param value the value
     * @return the binary16 bits
     */
    public static short fromFloat(float value) {
        int x = Float.floatToRawIntBits(value);
        int sign = (x >>> 16) & 0x8000;
        int exp = (x >>> 23) & 0xFF;
        int mant = x & 0x7F_FFFF;
        if (exp == 0xFF) {
            if (mant == 0) {
                return (short) (sign | 0x7C00);
            }
            return (short) (sign | 0x7E00 | (mant >>> 13));
        }
        if (exp == 0) {
            return (short) sign; // a float subnormal is far below the smallest binary16 subnormal
        }
        int e = exp - 127 + 15;
        if (e >= 31) {
            return (short) (sign | 0x7C00);
        }
        if (e <= 0) {
            int shift = 14 - e;
            if (shift > 24) {
                return (short) sign;
            }
            int m = mant | 0x80_0000;
            int r = m >>> shift;
            int rem = m & ((1 << shift) - 1);
            int half = 1 << (shift - 1);
            if (rem > half || (rem == half && (r & 1) != 0)) {
                r++;
            }
            return (short) (sign | r);
        }
        int r = (e << 10) | (mant >>> 13);
        int rem = mant & 0x1FFF;
        if (rem > 0x1000 || (rem == 0x1000 && (r & 1) != 0)) {
            r++; // a carry into the exponent is correct, up to infinity
        }
        return (short) (sign | r);
    }

    /**
     * Rounds a {@code float} to the nearest binary16 value and returns it as a {@code float}.
     *
     * @param value the value
     * @return the rounded value
     */
    public static float round(float value) {
        return toFloat(fromFloat(value));
    }
}
