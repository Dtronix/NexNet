package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.NexusDateOnly;
import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeKind;
import com.dtronix.nexnet.NexusDateTimeOffset;
import com.dtronix.nexnet.NexusDecimal;
import com.dtronix.nexnet.NexusTimeOnly;
import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.UnsignedLongs;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.time.Duration;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalTime;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.Random;
import java.util.TimeZone;
import org.junit.jupiter.api.Test;

/**
 * The value types' conversions, checked against {@code java.time}, {@code BigDecimal} and the JDK's unsigned helpers
 * (fine in tests: only the runtime must avoid API-26 classes).
 */
class ValueConversionsTest {
    private final Random random = new Random(7);

    @Test
    void dateTimeMatchesInstant() {
        assertEquals(NexusDateTime.UNIX_EPOCH_TICKS, NexusDateTime.fromEpochMillisUtc(0).ticks());
        for (int i = 0; i < 10_000; i++) {
            long ticks = Math.floorMod(random.nextLong(), NexusDateTime.MAX_TICKS + 1);
            NexusDateTime dt = NexusDateTime.of(ticks, NexusDateTimeKind.UTC);
            Instant instant = Instant.ofEpochSecond(dt.toEpochSecond(), dt.nanoOfSecond());
            assertEquals(instant.toEpochMilli(), dt.toEpochMillis());
            assertEquals(dt, NexusDateTime.ofEpochSecond(instant.getEpochSecond(), instant.getNano(),
                NexusDateTimeKind.UTC));
            assertEquals(dt, NexusDateTime.fromBinary(dt.toBinary()));
        }
        assertEquals(LocalDate.of(1, 1, 1), LocalDate.ofEpochDay(Math.floorDiv(
            NexusDateTime.of(0, NexusDateTimeKind.UTC).toEpochSecond(), 86_400L)));
        assertThrows(IllegalArgumentException.class, () -> NexusDateTime.fromEpochMillisUtc(Long.MAX_VALUE));
        assertThrows(IllegalArgumentException.class, () -> NexusDateTime.fromBinary(0x2BCA2875F4374000L));
        NexusDateTime local = NexusDateTime.fromBinary(0xC8D78E4D8B7C0000L);
        assertEquals(NexusDateTimeKind.LOCAL, local.kind());
        assertEquals(0x88D78E4D8B7C0000L, local.toBinary(), "kind 11 is written as 10");
        TimeZone plusTwo = TimeZone.getTimeZone("GMT+02:00");
        assertEquals(local.ticks() + 2 * 36_000_000_000L, local.localTicks(plusTwo));
    }

    @Test
    void dateTimeOffsetMatchesOffsetDateTime() {
        for (int i = 0; i < 10_000; i++) {
            long seconds = 1_000_000_000L + random.nextInt(Integer.MAX_VALUE);
            int nanos = random.nextInt(10_000_000) * 100;
            short offset = (short) (random.nextInt(1681) - 840);
            NexusDateTimeOffset v = NexusDateTimeOffset.ofEpochSecond(seconds, nanos, offset);
            OffsetDateTime odt = OffsetDateTime.ofInstant(Instant.ofEpochSecond(v.toEpochSecond(), v.nanoOfSecond()),
                ZoneOffset.ofTotalSeconds(v.offsetMinutes() * 60));
            assertEquals(seconds, odt.toEpochSecond());
            assertEquals(nanos, odt.getNano());
            assertEquals(v, NexusDateTimeOffset.ofUtc(v.utcTicks(), v.offsetMinutes()));
        }
        assertThrows(IllegalArgumentException.class, () -> NexusDateTimeOffset.ofUtc(0, (short) 841));
        assertThrows(IllegalArgumentException.class, () -> NexusDateTimeOffset.ofUtc(0, (short) -60));
        assertFalse(NexusDateTimeOffset.of(5, (short) 60).isValid());
    }

    @Test
    void dateOnlyTimeOnlyAndTimeSpanMatchJavaTime() {
        assertEquals(LocalDate.of(2020, 1, 1).toEpochDay(), NexusDateOnly.of(737424).toEpochDay());
        assertEquals(LocalDate.of(9999, 12, 31).toEpochDay(), NexusDateOnly.of(NexusDateOnly.MAX_DAY_NUMBER)
            .toEpochDay());
        assertEquals(NexusDateOnly.of(0), NexusDateOnly.ofEpochDay(LocalDate.of(1, 1, 1).toEpochDay()));
        assertThrows(IllegalArgumentException.class, () -> NexusDateOnly.ofEpochDay(LocalDate.of(10000, 1, 1)
            .toEpochDay()));
        assertEquals(LocalTime.MAX.toNanoOfDay() / 100, NexusTimeOnly.ofNanoOfDay(LocalTime.MAX.toNanoOfDay()).ticks());
        assertTrue(NexusTimeOnly.of(NexusTimeOnly.MAX_TICKS).isValid());
        assertFalse(NexusTimeOnly.of(NexusTimeOnly.MAX_TICKS + 1).isValid());
        for (int i = 0; i < 10_000; i++) {
            long ticks = random.nextLong() / 1000;
            NexusTimeSpan span = NexusTimeSpan.of(ticks);
            Duration d = Duration.ofSeconds(span.toSeconds(), span.nanoAdjustment());
            assertEquals(ticks, d.toNanos() / 100);
            assertEquals(span, NexusTimeSpan.ofSeconds(d.getSeconds(), d.getNano()));
        }
    }

    @Test
    void decimalConversionsMatchBigDecimal() {
        for (int i = 0; i < 10_000; i++) {
            BigInteger unscaled = new BigInteger(1 + random.nextInt(96), random);
            if (random.nextBoolean()) {
                unscaled = unscaled.negate();
            }
            BigDecimal value = new BigDecimal(unscaled, random.nextInt(29));
            NexusDecimal d = NexusDecimal.fromBigDecimal(value);
            assertTrue(NexusDecimal.isValidFlags(d.flags()));
            assertEquals(value, d.toBigDecimal());
        }
        assertFalse(NexusDecimal.isValidFlags(1 << 24));
        assertFalse(NexusDecimal.isValidFlags(29 << 16));
        assertTrue(NexusDecimal.of(0, 0, 0, 0x8000_0000).isNegative());
    }

    @Test
    void unsignedLongsMatchTheJdk() {
        long[] edges = {0, 1, -1, Long.MAX_VALUE, Long.MIN_VALUE, 10, -10, 0xFFFF_FFFFL};
        for (int i = 0; i < 20_000; i++) {
            long a = i < edges.length ? edges[i] : random.nextLong();
            long b = i < edges.length ? edges[edges.length - 1 - i] : (random.nextBoolean() ? random.nextLong()
                : random.nextInt(1000) + 1);
            assertEquals(Long.toUnsignedString(a), UnsignedLongs.toString(a));
            assertEquals(a, UnsignedLongs.parse(Long.toUnsignedString(a)));
            assertEquals(Integer.signum(Long.compareUnsigned(a, b)), Integer.signum(UnsignedLongs.compare(a, b)));
            assertEquals(new BigInteger(Long.toUnsignedString(a)), UnsignedLongs.toBigInteger(a));
            assertEquals(a, UnsignedLongs.fromBigInteger(UnsignedLongs.toBigInteger(a)));
            if (b != 0) {
                assertEquals(Long.divideUnsigned(a, b), UnsignedLongs.divide(a, b));
                assertEquals(Long.remainderUnsigned(a, b), UnsignedLongs.remainder(a, b));
            }
        }
        assertEquals(-1L, UnsignedLongs.parse("18446744073709551615"));
        assertThrows(NumberFormatException.class, () -> UnsignedLongs.parse("18446744073709551616"));
        assertThrows(NumberFormatException.class, () -> UnsignedLongs.parse("-1"));
        assertThrows(NumberFormatException.class, () -> UnsignedLongs.parse(""));
        assertThrows(NumberFormatException.class, () -> UnsignedLongs.parse("99999999999999999999"));
        assertThrows(ArithmeticException.class, () -> UnsignedLongs.fromBigInteger(BigInteger.ONE.shiftLeft(64)));
    }
}
