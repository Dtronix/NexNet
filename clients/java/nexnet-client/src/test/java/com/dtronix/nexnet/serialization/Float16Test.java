package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Random;
import org.junit.jupiter.api.Test;

/** {@link Float16} against a brute-force reference. */
class Float16Test {
    /** Every finite positive binary16 value in increasing order: bits 0x0000..0x7BFF. */
    private static final float[] POSITIVE = new float[0x7C00];

    static {
        for (int h = 0; h < POSITIVE.length; h++) {
            POSITIVE[h] = Float16.toFloat((short) h);
        }
    }

    /** Round to nearest binary16, ties to even, by searching the table. */
    private static short reference(float f) {
        int sign = Float.floatToRawIntBits(f) < 0 ? 0x8000 : 0;
        if (Float.isNaN(f)) {
            throw new IllegalArgumentException();
        }
        double a = Math.abs((double) f);
        if (a >= 65520.0) {
            return (short) (sign | 0x7C00);
        }
        int lo = 0;
        int hi = POSITIVE.length - 1;
        while (lo < hi) {
            int mid = (lo + hi + 1) >>> 1;
            if (POSITIVE[mid] <= a) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        int below = lo;
        if (POSITIVE[below] == a || below == POSITIVE.length - 1) {
            return (short) (sign | below);
        }
        double down = a - POSITIVE[below];
        double up = POSITIVE[below + 1] - a;
        int pick = down < up ? below : up < down ? below + 1 : (below % 2 == 0 ? below : below + 1);
        return (short) (sign | pick);
    }

    @Test
    void everyBinary16ValueRoundTrips() {
        for (int h = 0; h <= 0xFFFF; h++) {
            short bits = (short) h;
            float f = Float16.toFloat(bits);
            if (Float.isNaN(f)) {
                // NaN keeps its sign and payload and is made quiet.
                assertEquals((short) (h | 0x0200), Float16.fromFloat(f), Integer.toHexString(h));
            } else {
                assertEquals(bits, Float16.fromFloat(f), Integer.toHexString(h));
            }
        }
    }

    @Test
    void roundingMatchesTheReference() {
        Random random = new Random(42);
        for (int i = 0; i < 1_000_000; i++) {
            float f;
            if (i % 2 == 0) {
                f = Float.intBitsToFloat(random.nextInt());
            } else {
                // Values in the binary16 range, including subnormals and ties.
                f = (float) ((random.nextDouble() - 0.5) * Math.pow(2, random.nextInt(40) - 25));
            }
            if (Float.isNaN(f)) {
                assertTrue(Float.isNaN(Float16.toFloat(Float16.fromFloat(f))));
                continue;
            }
            assertEquals(reference(f), Float16.fromFloat(f), () -> Float.toString(f));
        }
        // Exact ties around 1: 1 + 2^-11 rounds down to even, 1 + 3 * 2^-11 up to even.
        assertEquals(0x3C00, Float16.fromFloat(1f + 0x1p-11f));
        assertEquals(0x3C02, Float16.fromFloat(1f + 3 * 0x1p-11f));
        assertEquals(0x7C00, Float16.fromFloat(65520f));
        assertEquals(0x7BFF, Float16.fromFloat(65519.996f));
        assertEquals(0x0000, Float16.fromFloat(0x1p-25f));
        assertEquals(0x0001, Float16.fromFloat(0x1.000002p-25f));
    }
}
