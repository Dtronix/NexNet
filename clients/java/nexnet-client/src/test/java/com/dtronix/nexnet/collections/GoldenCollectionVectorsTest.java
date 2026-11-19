package com.dtronix.nexnet.collections;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.fail;

import com.dtronix.nexnet.invocation.PayloadCodec;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.Formatters;
import com.dtronix.nexnet.serialization.GoldenFiles;
import com.dtronix.nexnet.serialization.MsgPackException;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;

/**
 * Replays the collection-sync golden vectors (K26, {@code collections.jsonl} and the collection reject vectors)
 * through {@link CollectionWire}, the item codec of {@link NexusListClient}, with the runtime's MessagePack payload
 * codec: every item decodes to the golden fields and embedded values, the requests a client sends (Clear, Insert,
 * Replace, Move, Remove, flags 0) encode to the golden bytes, and reject vectors are refused. The resets, Noop and
 * Ack items are server-sent: decoded only.
 */
class GoldenCollectionVectorsTest {
    private static final PayloadCodec CODEC = PayloadCodec.MSGPACK;
    private static final Formatter<String> STRING = Formatters.nullable(Formatters.STRING);

    @TestFactory
    Stream<DynamicTest> replay() {
        return GoldenFiles.of(GoldenFiles.COLLECTION_TYPES).stream()
            .map(v -> DynamicTest.dynamicTest(v.toString(), () -> replay(v)));
    }

    private static void replay(GoldenFiles.Vector v) {
        if (!v.type.equals("CollectionListMessage")) {
            fail("Not a collection vector type: " + v.type);
        }
        if (v.isReject()) {
            reject(v);
            return;
        }
        List<Object> value = v.list();
        int tag = GoldenFiles.int32(value.get(0));
        List<Object> fields = GoldenFiles.list(value.get(1));
        int flags = GoldenFiles.int32(fields.get(0));
        switch (tag) {
            case CollectionWire.RESET_VALUES:
                resetValues(v, fields);
                return;
            case CollectionWire.INSERT:
            case CollectionWire.REPLACE:
                valueItem(v, tag, fields);
                return;
            default:
                break;
        }
        CollectionWire.Message<Integer> m = CollectionWire.decode(v.bytes(), CODEC, Formatters.INT);
        assertEquals(tag, m.tag, "tag");
        assertEquals(flags, m.flags, "flags");
        byte[] request = null;
        switch (tag) {
            case CollectionWire.RESET_START:
                assertEquals(GoldenFiles.int32(fields.get(1)), m.version, "version");
                assertEquals(GoldenFiles.int32(fields.get(2)), m.index, "totalValues");
                break;
            case CollectionWire.RESET_COMPLETE:
            case CollectionWire.NOOP:
                break;
            case CollectionWire.CLEAR:
                assertEquals(GoldenFiles.int32(fields.get(1)), m.version, "version");
                request = CollectionWire.clearRequest(m.version);
                break;
            case CollectionWire.MOVE:
                assertEquals(GoldenFiles.int32(fields.get(1)), m.version, "version");
                assertEquals(GoldenFiles.int32(fields.get(2)), m.index, "fromIndex");
                assertEquals(GoldenFiles.int32(fields.get(3)), m.toIndex, "toIndex");
                request = CollectionWire.moveRequest(m.version, m.index, m.toIndex);
                break;
            case CollectionWire.REMOVE:
                assertEquals(GoldenFiles.int32(fields.get(1)), m.version, "version");
                assertEquals(GoldenFiles.int32(fields.get(2)), m.index, "index");
                request = CollectionWire.removeRequest(m.version, m.index);
                break;
            default:
                fail("Unknown tag " + tag);
        }
        encodeRequest(v, flags, request);
    }

    /** A request the client sends (flags 0) encodes to the golden bytes. */
    private static void encodeRequest(GoldenFiles.Vector v, int flags, byte[] request) {
        if (request != null && flags == 0 && v.mode.equals("both")) {
            assertEquals(GoldenFiles.hex(v.bytes()), GoldenFiles.hex(request), "encode");
        }
    }

    /** Insert and Replace: {@code [flags, version, index, value]}; the value is an int, a string or nil. */
    private static void valueItem(GoldenFiles.Vector v, int tag, List<Object> fields) {
        Object json = fields.get(3);
        byte[] raw = json == null ? new byte[] {(byte) 0xC0} : GoldenFiles.bytes(json);
        boolean integer = (raw[0] & 0x80) == 0 || (raw[0] & 0xFF) >= 0xCC && (raw[0] & 0xFF) <= 0xD3
            || (raw[0] & 0xE0) == 0xE0;
        if (integer) {
            valueItem(v, tag, fields, raw, Formatters.INT);
        } else {
            valueItem(v, tag, fields, raw, STRING);
        }
    }

    private static <T> void valueItem(GoldenFiles.Vector v, int tag, List<Object> fields, byte[] raw,
                                      Formatter<T> formatter) {
        CollectionWire.Message<T> m = CollectionWire.decode(v.bytes(), CODEC, formatter);
        int flags = GoldenFiles.int32(fields.get(0));
        assertEquals(tag, m.tag, "tag");
        assertEquals(flags, m.flags, "flags");
        assertEquals(GoldenFiles.int32(fields.get(1)), m.version, "version");
        assertEquals(GoldenFiles.int32(fields.get(2)), m.index, "index");
        assertEquals(GoldenFiles.hex(raw), GoldenFiles.hex(CODEC.writeValue(formatter, m.value)), "value");
        encodeRequest(v, flags, CollectionWire.valueRequest(tag, m.version, m.index, m.value, CODEC, formatter));
    }

    /**
     * ResetValues: {@code [flags, values]}; the values are an ext 78 {@code int} array or a {@code string} array,
     * which the item decoder splits into values: the same values the codec reads from the field as a whole.
     */
    private static void resetValues(GoldenFiles.Vector v, List<Object> fields) {
        byte[] raw = GoldenFiles.bytes(fields.get(1));
        int first = raw[0] & 0xFF;
        boolean array = (first & 0xF0) == 0x90 || first == 0xDC || first == 0xDD;
        List<Object> expected = new ArrayList<>();
        CollectionWire.Message<?> m;
        if (array) {
            expected.addAll(CODEC.readValue(Formatters.list(STRING), raw, 0, raw.length));
            m = CollectionWire.decode(v.bytes(), CODEC, STRING);
        } else {
            for (int value : CODEC.readValue(Formatters.INT_ARRAY, raw, 0, raw.length)) {
                expected.add(value);
            }
            m = CollectionWire.decode(v.bytes(), CODEC, Formatters.INT);
        }
        List<?> values = m.values;
        assertEquals(CollectionWire.RESET_VALUES, m.tag, "tag");
        assertEquals(GoldenFiles.int32(fields.get(0)), m.flags, "flags");
        assertEquals(expected, values, "values");
    }

    private static void reject(GoldenFiles.Vector v) {
        assertThrows(MsgPackException.class, () -> CollectionWire.decode(v.bytes(), CODEC, Formatters.INT), "refused");
    }
}
