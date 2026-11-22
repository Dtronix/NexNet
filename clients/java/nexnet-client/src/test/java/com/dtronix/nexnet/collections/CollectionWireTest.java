package com.dtronix.nexnet.collections;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

/** The collection item codec against items recorded from the C# runtime. */
class CollectionWireTest {
    private static final CollectionCodec CODEC = CollectionCodec.INSTANCE;

    private static <T> CollectionWire.Message<T> decode(String fixture, Formatter<T> formatter) {
        return CollectionWire.decode(CollectionFixtures.get(fixture), CODEC, formatter);
    }

    private static <T> void assertMessage(CollectionWire.Message<T> m, int tag, int flags, int version, int index,
                                          int toIndex) {
        assertEquals(tag, m.tag, "tag");
        assertEquals(flags, m.flags, "flags");
        assertEquals(version, m.version, "version");
        assertEquals(index, m.index, "index");
        assertEquals(toIndex, m.toIndex, "toIndex");
    }

    @Test
    void everyServerItemDecodes() {
        for (Map.Entry<String, byte[]> e : CollectionFixtures.all().entrySet()) {
            String name = e.getKey();
            Formatter<?> formatter = name.contains("string") ? TestFormat.STRING
                : name.endsWith("bool") ? TestFormat.BOOL
                : name.endsWith("float") || name.endsWith("double") || name.endsWith("half") ? TestFormat.DOUBLE
                : TestFormat.LONG;
            CollectionWire.decode(e.getValue(), CODEC, formatter);
        }
    }

    @Test
    void resetItems() {
        assertMessage(decode("reset-start", TestFormat.LONG), CollectionWire.RESET_START, 0, 7, 3, 0);
        assertMessage(decode("reset-start-empty", TestFormat.LONG), CollectionWire.RESET_START, 0, 0, 0, 0);
        CollectionWire.Message<Long> large = decode("reset-start-large", TestFormat.LONG);
        assertMessage(large, CollectionWire.RESET_START, 1, 70000, 300, 0);
        assertTrue(large.isAck());
        assertMessage(decode("reset-start-negative", TestFormat.LONG), CollectionWire.RESET_START, 0, 1, -1, 0);
        assertMessage(decode("reset-complete", TestFormat.LONG), CollectionWire.RESET_COMPLETE, 0, 0, 0, 0);
        assertFalse(decode("reset-complete", TestFormat.LONG).isChange());
    }

    private static <T> List<T> values(String fixture, Formatter<T> formatter) {
        CollectionWire.Message<T> m = decode(fixture, formatter);
        assertEquals(CollectionWire.RESET_VALUES, m.tag);
        assertEquals(0, m.flags);
        return m.values;
    }

    @Test
    void resetValuesOfEveryArrayEncoding() {
        assertEquals(Arrays.asList(1L, 2L, -5L), values("reset-values-int", TestFormat.LONG));
        assertEquals(new ArrayList<Long>(), values("reset-values-int-empty", TestFormat.LONG));
        List<Long> forty = new ArrayList<>();
        for (long i = 0; i < 40; i++) {
            forty.add(i);
        }
        assertEquals(forty, values("reset-values-int-40", TestFormat.LONG));
        assertEquals(Arrays.asList("a", "hello", null), values("reset-values-string", TestFormat.STRING));
        // byte[] is bin; each byte is handed to the item formatter as an unsigned integer.
        assertEquals(Arrays.asList(0L, 255L, 7L), values("reset-values-byte", TestFormat.LONG));
        assertEquals(Arrays.asList(-128L, 127L), values("reset-values-sbyte", TestFormat.LONG));
        assertEquals(Arrays.asList(-32768L, 300L), values("reset-values-short", TestFormat.LONG));
        assertEquals(Arrays.asList(65535L, 1L), values("reset-values-ushort", TestFormat.LONG));
        assertEquals(Arrays.asList(4294967295L, 0L), values("reset-values-uint", TestFormat.LONG));
        assertEquals(Arrays.asList(Long.MIN_VALUE, Long.MAX_VALUE, 0L), values("reset-values-long", TestFormat.LONG));
        // ulong.MaxValue as uint64: the bits of -1.
        assertEquals(Arrays.asList(-1L, 5L), values("reset-values-ulong", TestFormat.LONG));
        assertEquals(Arrays.asList(1.5, -0.25), values("reset-values-float", TestFormat.DOUBLE));
        assertEquals(Arrays.asList(3.25, -1e300), values("reset-values-double", TestFormat.DOUBLE));
        assertEquals(Arrays.asList(65L, 233L), values("reset-values-char", TestFormat.LONG));
        assertEquals(Arrays.asList(1.5, -2.0, (double) 6.1035156E-5f, (double) 5.9604645E-8f),
            values("reset-values-half", TestFormat.DOUBLE));
        assertEquals(Arrays.asList(true, false), values("reset-values-bool", TestFormat.BOOL));
    }

    @Test
    void changeItems() {
        assertMessage(decode("clear", TestFormat.LONG), CollectionWire.CLEAR, 0, 5, 0, 0);
        assertMessage(decode("clear-ack", TestFormat.LONG), CollectionWire.CLEAR, 1, 6, 0, 0);
        CollectionWire.Message<Long> insert = decode("insert-int", TestFormat.LONG);
        assertMessage(insert, CollectionWire.INSERT, 0, 8, 2, 0);
        assertEquals(42L, insert.value);
        assertTrue(insert.isChange());
        CollectionWire.Message<Long> insertAck = decode("insert-int-ack", TestFormat.LONG);
        assertMessage(insertAck, CollectionWire.INSERT, 1, 9, 0, 0);
        assertEquals(-1L, insertAck.value);
        CollectionWire.Message<Long> large = decode("insert-int-large", TestFormat.LONG);
        assertMessage(large, CollectionWire.INSERT, 0, 1_000_000, 65536, 0);
        assertEquals((long) Integer.MIN_VALUE, large.value);
        assertEquals("x", decode("insert-string", TestFormat.STRING).value);
        CollectionWire.Message<Long> replace = decode("replace-int", TestFormat.LONG);
        assertMessage(replace, CollectionWire.REPLACE, 0, 11, 1, 0);
        assertEquals(99L, replace.value);
        CollectionWire.Message<String> replaceAck = decode("replace-string-ack", TestFormat.STRING);
        assertMessage(replaceAck, CollectionWire.REPLACE, 1, 12, 0, 0);
        assertEquals("y", replaceAck.value);
        assertMessage(decode("move", TestFormat.LONG), CollectionWire.MOVE, 0, 13, 3, 0);
        assertMessage(decode("move-ack", TestFormat.LONG), CollectionWire.MOVE, 1, 14, 0, 2);
        assertMessage(decode("remove", TestFormat.LONG), CollectionWire.REMOVE, 0, 15, 1, 0);
        assertMessage(decode("remove-ack", TestFormat.LONG), CollectionWire.REMOVE, 1, 16, 0, 0);
        assertMessage(decode("noop", TestFormat.LONG), CollectionWire.NOOP, 0, 0, 0, 0);
        assertMessage(decode("noop-ack", TestFormat.LONG), CollectionWire.NOOP, 1, 0, 0, 0);
    }

    @Test
    void requestsMatchTheCSharpClient() {
        assertArrayEquals(CollectionFixtures.get("request-insert-int"),
            CollectionWire.valueRequest(CollectionWire.INSERT, 4, 2, 42L, CODEC, TestFormat.LONG));
        assertArrayEquals(CollectionFixtures.get("request-insert-string"),
            CollectionWire.valueRequest(CollectionWire.INSERT, 300, 1, "hello", CODEC, TestFormat.STRING));
        assertArrayEquals(CollectionFixtures.get("request-replace-int"),
            CollectionWire.valueRequest(CollectionWire.REPLACE, 5, 0, -7L, CODEC, TestFormat.LONG));
        assertArrayEquals(CollectionFixtures.get("request-move"), CollectionWire.moveRequest(6, 2, 0));
        assertArrayEquals(CollectionFixtures.get("request-remove"), CollectionWire.removeRequest(70000, 1));
        assertArrayEquals(CollectionFixtures.get("request-clear"), CollectionWire.clearRequest(0));
    }

    @Test
    void theCollectionInvocationArgumentIsThePipeId() {
        assertArrayEquals(new byte[] {(byte) 0x91, 7}, CollectionWire.pipeArguments(7));
        assertArrayEquals(new byte[] {(byte) 0x91, (byte) 0xCC, (byte) 200}, CollectionWire.pipeArguments(200));
    }

    @Test
    void malformedItemsAreRejected() {
        String[] bad = {
            "",                         // empty
            "c0",                       // nil union
            "93000000",                 // three-element union
            "920993000000",             // unknown tag 9
            "92cdffff9100",             // tag 65535
            "92009200",                 // ResetStart with too few fields
            "9202910100",               // trailing bytes
            "920291cd0100",             // flags 256
            "9200930001",               // truncated
            "92019200c7024f0400",       // values with ext type 79
            "92019200c7034e040102",     // int32 values with a partial element
            "92019200c7024e0c00",       // unknown element kind 12
            "9200930001ce80000000",     // totalValues above int32
            "920192009102",             // a value the item formatter rejects (an integer for a string)
        };
        for (String hex : bad) {
            byte[] item = CollectionFixtures.hex(hex);
            assertThrows(MsgPackException.class, () -> CollectionWire.decode(item, CODEC, TestFormat.STRING), hex);
        }
    }

    @Test
    void extraFieldsAreRefused() {
        // Remove [0, version 15, index 1, extra "x", extra [1, 2]]: bodies have their exact element count.
        assertThrows(MsgPackException.class, () -> CollectionWire.decode(
            CollectionFixtures.hex("92079500" + "0f01" + "a178" + "920102"), CODEC, TestFormat.LONG));
    }

    @Test
    void nilValuesDecodeToNoValues() {
        CollectionWire.Message<Long> m = CollectionWire.decode(CollectionFixtures.hex("92019200c0"), CODEC,
            TestFormat.LONG);
        assertNull(m.values);
    }

    @Test
    void valueFormatterFailuresAreDecodeErrors() {
        assertThrows(MsgPackException.class, () -> decode("insert-int", TestFormat.STRING));
    }

    @Test
    void halfConversion() {
        assertEquals(0f, CollectionWire.halfToFloat(0));
        assertEquals(Float.floatToRawIntBits(-0f), Float.floatToRawIntBits(CollectionWire.halfToFloat(0x8000)));
        assertEquals(65504f, CollectionWire.halfToFloat(0x7BFF));
        assertEquals(Float.POSITIVE_INFINITY, CollectionWire.halfToFloat(0x7C00));
        assertEquals(Float.NEGATIVE_INFINITY, CollectionWire.halfToFloat(0xFC00));
        assertTrue(Float.isNaN(CollectionWire.halfToFloat(0x7E00)));
        assertEquals(1f, CollectionWire.halfToFloat(0x3C00));
        assertEquals((float) Math.pow(2, -24), CollectionWire.halfToFloat(0x0001));
        assertEquals((float) (1023 * Math.pow(2, -24)), CollectionWire.halfToFloat(0x03FF));
    }
}
