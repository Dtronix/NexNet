package com.dtronix.nexnet.serialization;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeKind;
import com.dtronix.nexnet.NexusDecimal;
import com.dtronix.nexnet.Tuple2;
import com.dtronix.nexnet.Tuple3;
import com.dtronix.nexnet.serialization.MsgPackException.Kind;
import java.math.BigDecimal;
import java.util.AbstractMap;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import org.junit.jupiter.api.Test;

/** Reader, writer and formatter behavior beyond the golden vectors. */
class MsgPackCodecTest {
    private static String hex(byte[] bytes) {
        return GoldenVectorTests.hex(bytes);
    }

    private static byte[] bytes(String hex) {
        return GoldenVectorTests.unhex(hex);
    }

    private static <T> T roundTrip(Formatter<T> formatter, T value) {
        return Formatters.deserialize(formatter, Formatters.serialize(formatter, value));
    }

    private static MsgPackException.Kind kindOf(Runnable action) {
        return assertThrows(MsgPackException.class, action::run).kind();
    }

    @Test
    void writerUsesTheCanonicalIntegerForms() {
        MsgPackWriter w = new MsgPackWriter();
        long[] values = {0, 127, 128, 255, 256, 65535, 65536, 0xFFFF_FFFFL, 0x1_0000_0000L, -1, -32, -33, -128, -129,
            -32768, -32769, Integer.MIN_VALUE, Integer.MIN_VALUE - 1L, Long.MIN_VALUE, Long.MAX_VALUE};
        for (long v : values) {
            w.writeInt(v);
        }
        assertEquals("007fcc80ccffcd0100cdffffce00010000ceffffffffcf0000000100000000ffe0d0dfd080d1ff7fd18000"
            + "d2ffff7fffd280000000d3ffffffff7fffffffd38000000000000000cf7fffffffffffffff", hex(w.toByteArray()));
        w.reset();
        w.writeUInt64(-1L);
        w.writeUInt64(5);
        assertEquals("cfffffffffffffffff05", hex(w.toByteArray()));
        w.close();
    }

    @Test
    void writerWritesUnpairedSurrogatesAsReplacementCharacters() {
        MsgPackWriter w = new MsgPackWriter();
        w.writeString("a" + (char) 0xD800 + "b" + (char) 0xDC00);
        assertEquals("a861efbfbd62efbfbd", hex(w.toByteArray()));
        w.reset();
        String emoji = new String(Character.toChars(0x1F600));
        w.writeString(emoji);
        assertEquals("a4f09f9880", hex(w.toByteArray()));
        assertEquals(emoji, new MsgPackReader(w.toByteArray()).readString());
    }

    @Test
    void writerGrowsAndReusesItsBuffer() {
        MsgPackWriter w = new MsgPackWriter();
        byte[] big = new byte[100_000];
        Arrays.fill(big, (byte) 7);
        w.writeBinary(big);
        assertEquals(5 + big.length, w.size());
        byte[] out = w.toByteArray();
        assertEquals("c6000186a0", hex(Arrays.copyOf(out, 5)));
        w.close();
        assertEquals(0, w.size());
        w.writeNil();
        assertArrayEquals(new byte[] {(byte) 0xC0}, w.toByteArray());
        assertThrows(IllegalArgumentException.class, () -> w.writeArrayHeader(-1));
        assertThrows(IndexOutOfBoundsException.class, () -> w.writeRaw(new byte[2], 1, 2));
    }

    @Test
    void extHeadersUseTheSmallestForm() {
        MsgPackWriter w = new MsgPackWriter();
        w.writeExtHeader(78, 1);
        w.writeExtHeader(78, 3);
        w.writeExtHeader(-1, 16);
        w.writeExtHeader(5, 256);
        w.writeExtHeader(5, 65536);
        assertEquals("d44ec7034ed8ffc8010005c90001000005", hex(w.toByteArray()));
    }

    @Test
    void peekTypeNamesEveryFamily() {
        String[] hexes = {"00", "ff", "cc01", "80", "90", "a0", "c0", "c2", "c4", "ca", "d4", "c7", "d9", "dc", "de"};
        MsgPackType[] types = {MsgPackType.INTEGER, MsgPackType.INTEGER, MsgPackType.INTEGER, MsgPackType.MAP,
            MsgPackType.ARRAY, MsgPackType.STRING, MsgPackType.NIL, MsgPackType.BOOLEAN, MsgPackType.BINARY,
            MsgPackType.FLOAT, MsgPackType.EXTENSION, MsgPackType.EXTENSION, MsgPackType.STRING, MsgPackType.ARRAY,
            MsgPackType.MAP};
        for (int i = 0; i < hexes.length; i++) {
            assertEquals(types[i], new MsgPackReader(bytes(hexes[i])).peekType(), hexes[i]);
        }
        assertEquals(Kind.RESERVED, kindOf(() -> new MsgPackReader(bytes("c1")).peekType()));
        assertEquals(Kind.TRUNCATED, kindOf(() -> new MsgPackReader(new byte[0]).peekType()));
    }

    @Test
    void skipIsIterativeAndReadRawValueReturnsOneValue() {
        int depth = 200_000;
        byte[] nested = new byte[depth + 1];
        Arrays.fill(nested, (byte) 0x91);
        nested[depth] = (byte) 0xC0;
        MsgPackReader deep = new MsgPackReader(nested);
        deep.skip();
        assertTrue(deep.isEnd());

        MsgPackReader r = new MsgPackReader(bytes("92a161c7024e0102" + "93"));
        assertEquals("92a161c7024e0102", hex(r.readRawValue()));
        assertEquals(1, r.remaining());
        assertEquals(Kind.TRUNCATED, kindOf(r::skip));
        assertEquals(Kind.RESERVED, kindOf(() -> new MsgPackReader(bytes("91c1")).skip()));
        assertEquals(Kind.TRUNCATED, kindOf(() -> new MsgPackReader(bytes("dd7fffffff")).skip()));
    }

    @Test
    void depthAndElementLimitsApplyToTheWholeValue() {
        Formatter<List<List<String>>> nested = Formatters.list(Formatters.list(Formatters.STRING));
        byte[] data = Formatters.serialize(nested, Arrays.asList(Arrays.asList("a", "b"), Arrays.asList("c")));
        assertEquals(2, Formatters.deserialize(nested, data, MsgPackOptions.UNTRUSTED.withMaxDepth(2)).size());
        assertEquals(Kind.DEPTH, kindOf(() -> Formatters.deserialize(nested, data,
            MsgPackOptions.UNTRUSTED.withMaxDepth(1))));
        // 2 + 2 + 1 elements.
        Formatters.deserialize(nested, data, MsgPackOptions.UNTRUSTED.withMaxElements(5));
        assertEquals(Kind.ELEMENTS, kindOf(() -> Formatters.deserialize(nested, data,
            MsgPackOptions.UNTRUSTED.withMaxElements(4))));
        assertEquals(Integer.MAX_VALUE, MsgPackOptions.TRUSTED.maxDepth());
        assertFalse(MsgPackOptions.TRUSTED.strictUtf8());
        assertThrows(IllegalArgumentException.class, () -> MsgPackOptions.UNTRUSTED.withMaxDepth(-1));
    }

    @Test
    void stringsAreStrictUnlessTheOptionsSayOtherwise() {
        byte[] invalid = bytes("a3618062");
        assertEquals(Kind.UTF8, kindOf(() -> Formatters.deserialize(Formatters.STRING, invalid)));
        assertEquals("a" + (char) 0xFFFD + "b", Formatters.deserialize(Formatters.STRING, invalid,
            MsgPackOptions.UNTRUSTED.withStrictUtf8(false)));
        assertEquals(Kind.TRAILING, kindOf(() -> Formatters.deserialize(Formatters.INT, bytes("0101"))));
    }

    @Test
    void unsignedAndStructFormattersCheckTheirValues() {
        assertEquals(Kind.RANGE, kindOf(() -> Formatters.serialize(Formatters.BYTE, 256)));
        assertEquals(Kind.RANGE, kindOf(() -> Formatters.serialize(Formatters.USHORT, -1)));
        assertEquals(Kind.RANGE, kindOf(() -> Formatters.serialize(Formatters.UINT, 1L << 32)));
        assertEquals(Kind.TYPE, kindOf(() -> Formatters.serialize(Formatters.INT, null)));
        assertEquals(Kind.TYPE, kindOf(() -> Formatters.deserialize(Formatters.GUID, bytes("c0"))));
        assertEquals(Kind.VALUE, kindOf(() -> Formatters.serialize(Formatters.DATE_TIME,
            NexusDateTime.of(-1, NexusDateTimeKind.UTC))));
        assertEquals("cf" + "ffffffffffffffff", hex(Formatters.serialize(Formatters.ULONG, -1L)));
        assertNull(roundTrip(Formatters.nullable(Formatters.INT), null));
        assertEquals(Integer.valueOf(-7), roundTrip(Formatters.nullable(Formatters.INT), -7));
        UUID id = UUID.randomUUID();
        assertEquals(id, roundTrip(Formatters.GUID, id));
    }

    @Test
    void halfArraysComeAsFloatsOrRawBits() {
        float[] floats = {1f, -2.5f, Float.POSITIVE_INFINITY, 0x1p-24f};
        byte[] data = Formatters.serialize(Formatters.HALF_ARRAY, floats);
        assertEquals("c7094e0b003c00c1007c0100", hex(data));
        assertArrayEquals(floats, Formatters.deserialize(Formatters.HALF_ARRAY, data));
        assertArrayEquals(new short[] {0x3C00, (short) 0xC100, 0x7C00, 1},
            Formatters.deserialize(Formatters.HALF_BITS_ARRAY, data));
        // A float that is not a binary16 value is rounded on write, as the C# Half it stands for.
        assertEquals(1f, roundTrip(Formatters.HALF, 1.0001f));
    }

    @Test
    void decimalsConvertToBigDecimalKeepingTheScale() {
        String[] values = {"0", "1", "-1", "1.50", "-1.5", "123.456", "79228162514264337593543950335",
            "-79228162514264337593543950335", "0.0000000000000000000000000001", "1E+3"};
        for (String text : values) {
            BigDecimal value = new BigDecimal(text);
            BigDecimal back = roundTrip(Formatters.DECIMAL, value);
            assertEquals(0, value.compareTo(back), text);
            assertEquals(Math.max(0, value.scale()), back.scale(), text);
        }
        assertEquals("c4100f000000000000000000000000000180", hex(Formatters.serialize(Formatters.DECIMAL,
            new BigDecimal("-1.5"))));
        // Negative zero is a decimal, not a BigDecimal.
        assertEquals(BigDecimal.ZERO, Formatters.deserialize(Formatters.DECIMAL,
            bytes("c41000000000000000000000000000000080")));
        assertEquals(Kind.RANGE, kindOf(() -> Formatters.serialize(Formatters.DECIMAL,
            new BigDecimal("79228162514264337593543950336"))));
        assertEquals(Kind.RANGE, kindOf(() -> Formatters.serialize(Formatters.DECIMAL,
            new BigDecimal("0.00000000000000000000000000001"))));
        // Trailing zeros beyond the limits are dropped rather than refused.
        assertEquals(new BigDecimal("1"), NexusDecimal.fromBigDecimal(new BigDecimal("1.00000000000000000000000000000"))
            .toBigDecimal());
        assertEquals(Kind.VALUE, kindOf(() -> Formatters.serialize(Formatters.NEXUS_DECIMAL,
            NexusDecimal.of(1, 0, 0, 29 << 16))));
    }

    @Test
    void containerFactoriesRoundTrip() {
        List<String> list = Arrays.asList("a", null, "c");
        assertEquals(list, roundTrip(Formatters.list(Formatters.STRING), list));
        Set<Integer> set = new LinkedHashSet<>(Arrays.asList(3, 1, 2));
        assertEquals(Arrays.asList(3, 1, 2), Arrays.asList(roundTrip(Formatters.set(Formatters.INT), set)
            .toArray()));
        Map<String, Long> map = new LinkedHashMap<>();
        map.put("x", 1L);
        map.put("y", null);
        assertEquals(map, roundTrip(Formatters.map(Formatters.STRING, Formatters.nullable(Formatters.LONG)), map));
        ArrayDeque<String> stack = new ArrayDeque<>();
        stack.push("a");
        stack.push("b");
        byte[] data = Formatters.serialize(Formatters.stack(Formatters.STRING), stack);
        assertEquals("92a162a161", hex(data), "top first");
        assertEquals("b", Formatters.deserialize(Formatters.stack(Formatters.STRING), data).pop());
        Map.Entry<String, Integer> entry = new AbstractMap.SimpleImmutableEntry<>("k", 9);
        assertEquals(entry, roundTrip(Formatters.keyValuePair(Formatters.STRING, Formatters.INT), entry));
        Tuple3<Integer, String, Double> tuple = Tuple3.of(1, "s", 2.5);
        Formatter<Tuple3<Integer, String, Double>> tf = Formatters.tuple(Formatters.INT, Formatters.STRING,
            Formatters.DOUBLE);
        assertEquals(tuple, roundTrip(tf, tuple));
        assertEquals(Kind.COUNT, kindOf(() -> Formatters.deserialize(Formatters.tuple(Formatters.INT,
            Formatters.INT), bytes("9101"))));
        Tuple2<Integer, Integer> pair = Tuple2.of(1, 2);
        assertEquals(pair, roundTrip(Formatters.tuple(Formatters.INT, Formatters.INT), pair));
    }

    @Test
    void theRegistryHoldsTheBuiltIns() {
        FormatterRegistry builtIn = FormatterRegistry.builtIn();
        assertSame(Formatters.INT, builtIn.get(Integer.class));
        assertSame(Formatters.GUID, builtIn.get(UUID.class));
        assertNull(builtIn.find(Thread.class));
        assertEquals(Kind.TYPE, kindOf(() -> builtIn.get(Thread.class)));
        assertThrows(UnsupportedOperationException.class, () -> builtIn.register(String.class, Formatters.STRING));
        FormatterRegistry mine = new FormatterRegistry();
        mine.registerAll(builtIn);
        mine.register(Integer.class, Formatters.BYTE);
        assertSame(Formatters.BYTE, mine.get(Integer.class));
        assertSame(Formatters.INT, builtIn.get(Integer.class));
    }
}
