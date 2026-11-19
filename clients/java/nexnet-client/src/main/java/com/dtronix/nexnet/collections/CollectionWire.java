package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.invocation.PayloadCodec;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackException;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.jspecify.annotations.Nullable;

/**
 * The items of the collection pipe (protocol specification sections 4.11 and 12.3): each channel item is a union
 * {@code [tag: uint16, [flags: uint8, ...fields]]}. The envelope is read and written here; embedded values go through
 * the item formatter with the session's {@link PayloadCodec}, so the channel itself moves the raw bytes of each item
 * ({@link #RAW}).
 */
final class CollectionWire {
    static final int RESET_START = 0;
    static final int RESET_VALUES = 1;
    static final int RESET_COMPLETE = 2;
    static final int CLEAR = 3;
    static final int INSERT = 4;
    static final int REPLACE = 5;
    static final int MOVE = 6;
    static final int REMOVE = 7;
    static final int NOOP = 8;

    /** Flags bit 0: the change answers this client's request. */
    static final int FLAG_ACK = 1;

    /** Ext type of a primitive array (specification section 10.5). */
    static final int EXT_PRIMITIVE_ARRAY = 78;

    /**
     * The channel item formatter: one item as its raw MessagePack bytes. The channel has already checked that each
     * item is exactly one value of its prefixed length.
     */
    static final Formatter<byte[]> RAW = new Formatter<byte[]>() {
        @Override
        public void write(MsgPackWriter writer, byte @Nullable [] value) {
            if (value == null) {
                throw new MsgPackException("A collection item is never null.");
            }
            writer.writeRaw(value, 0, value.length);
        }

        @Override
        public byte[] read(MsgPackReader reader) {
            return reader.readRaw(reader.remaining());
        }
    };

    /** The arguments of the collection invocation: {@code [pipeId: uint8]} (specification section 7.6). */
    static final PipeArgumentWriter PIPE_ARGUMENT = (writer, initialPipeId) -> {
        byte[] bytes = pipeArguments(initialPipeId);
        writer.writeRaw(bytes, 0, bytes.length);
    };

    private CollectionWire() {
    }

    /** One decoded item. */
    static final class Message<T> {
        final int tag;
        final int flags;
        /** ResetStart, Clear, Insert, Replace, Move, Remove. */
        final int version;
        /** Insert, Replace, Remove: the index. Move: the from index. ResetStart: the total value count. */
        final int index;
        /** Move: the to index. */
        final int toIndex;
        /** Insert, Replace. */
        final @Nullable T value;
        /** ResetValues; null when the values were nil. */
        final @Nullable List<@Nullable T> values;

        Message(int tag, int flags, int version, int index, int toIndex, @Nullable T value,
                @Nullable List<@Nullable T> values) {
            this.tag = tag;
            this.flags = flags;
            this.version = version;
            this.index = index;
            this.toIndex = toIndex;
            this.value = value;
            this.values = values;
        }

        boolean isAck() {
            return (flags & FLAG_ACK) != 0;
        }

        /** True for the changes (tags 3 to 8), which are buffered until the reset completes. */
        boolean isChange() {
            return tag >= CLEAR;
        }

        @Override
        public String toString() {
            return "CollectionMessage[tag=" + tag + ", flags=" + flags + ", version=" + version + ", index=" + index
                + ", toIndex=" + toIndex + "]";
        }
    }

    // ------------------------------------------------------------------ decoding

    /**
     * Decodes one item.
     *
     * @throws MsgPackException when the item is malformed, has an unknown tag, or a value fails to decode
     */
    static <T> Message<T> decode(byte[] item, PayloadCodec codec, Formatter<T> itemFormatter) {
        In in = new In(item);
        if (in.arrayHeader() != 2) {
            throw new MsgPackException("A collection item is not a two-element union.");
        }
        long tagValue = in.integer();
        if (tagValue < 0 || tagValue > 0xFFFF) {
            throw new MsgPackException("Collection item tag out of range: " + tagValue);
        }
        int tag = (int) tagValue;
        int count = in.arrayHeader();
        int needed = fieldCount(tag);
        if (count < needed) {
            throw new MsgPackException("Collection item " + tag + " has " + count + " fields, expected " + needed);
        }
        int flags = in.int32Range(0, 0xFF, "flags");
        int version = 0;
        int index = 0;
        int toIndex = 0;
        T value = null;
        List<T> values = null;
        switch (tag) {
            case RESET_START:
                version = in.int32();
                index = in.int32();
                break;
            case RESET_VALUES:
                values = readValues(in, codec, itemFormatter);
                break;
            case RESET_COMPLETE:
            case NOOP:
                break;
            case CLEAR:
                version = in.int32();
                break;
            case INSERT:
            case REPLACE:
                version = in.int32();
                index = in.int32();
                value = readValue(in, codec, itemFormatter);
                break;
            case MOVE:
                version = in.int32();
                index = in.int32();
                toIndex = in.int32();
                break;
            case REMOVE:
                version = in.int32();
                index = in.int32();
                break;
            default:
                throw new MsgPackException("Unknown collection item tag " + tag);
        }
        // Fields past the known ones are skipped (newer peers may append).
        for (int i = needed; i < count; i++) {
            in.skip();
        }
        if (in.remaining() != 0) {
            throw new MsgPackException("A collection item has trailing bytes.");
        }
        return new Message<>(tag, flags, version, index, toIndex, value, values);
    }

    private static int fieldCount(int tag) {
        switch (tag) {
            case RESET_START:
                return 3;
            case RESET_VALUES:
            case CLEAR:
                return 2;
            case RESET_COMPLETE:
            case NOOP:
                return 1;
            case INSERT:
            case REPLACE:
            case MOVE:
                return 4;
            case REMOVE:
                return 3;
            default:
                throw new MsgPackException("Unknown collection item tag " + tag);
        }
    }

    private static <T> @Nullable T readValue(In in, PayloadCodec codec, Formatter<T> itemFormatter) {
        int start = in.position;
        in.skip();
        return decodeValue(codec, itemFormatter, in.data, start, in.position - start);
    }

    private static <T> @Nullable T decodeValue(PayloadCodec codec, Formatter<T> itemFormatter, byte[] data,
                                               int offset, int length) {
        try {
            return codec.readValue(itemFormatter, data, offset, length);
        } catch (MsgPackException e) {
            throw e;
        } catch (RuntimeException e) {
            throw new MsgPackException("A collection value does not decode: " + e.getMessage(), e);
        }
    }

    /**
     * Reads {@code values: embedded T[]}. C# writes {@code T[]} as an array of values, as {@code bin} for
     * {@code byte}, or as an ext 78 primitive array for the primitive kinds (specification sections 10.4 and 10.5).
     * Each element is handed to the item formatter as its canonical MessagePack value.
     */
    private static <T> @Nullable List<@Nullable T> readValues(In in, PayloadCodec codec, Formatter<T> itemFormatter) {
        int b = in.peek();
        if (b == 0xC0) {
            in.position++;
            return null;
        }
        if ((b & 0xF0) == 0x90 || b == 0xDC || b == 0xDD) {
            int count = in.arrayHeader();
            // The count comes from the wire: each value takes at least one byte.
            if (count > in.remaining()) {
                throw new MsgPackException("A value array is longer than its item.");
            }
            List<T> values = new ArrayList<>(count);
            for (int i = 0; i < count; i++) {
                values.add(readValue(in, codec, itemFormatter));
            }
            return values;
        }
        if (b == 0xC4 || b == 0xC5 || b == 0xC6) {
            byte[] bytes = in.binary();
            List<T> values = new ArrayList<>(bytes.length);
            Out element = new Out();
            for (byte value : bytes) {
                element.reset();
                element.integer(value & 0xFF);
                values.add(decodeValue(codec, itemFormatter, element.buffer, 0, element.size));
            }
            return values;
        }
        int extType = in.extHeaderType(b);
        int length = in.extLength;
        if (extType != EXT_PRIMITIVE_ARRAY) {
            throw new MsgPackException("Unexpected ext type " + extType + " for a value array.");
        }
        return readPrimitiveArray(in.raw(length), codec, itemFormatter);
    }

    private static <T> List<@Nullable T> readPrimitiveArray(byte[] data, PayloadCodec codec,
                                                           Formatter<T> itemFormatter) {
        if (data.length < 1) {
            throw new MsgPackException("A primitive array has no element kind.");
        }
        int kind = data[0] & 0xFF;
        int size = elementSize(kind);
        int bytes = data.length - 1;
        if (bytes % size != 0) {
            throw new MsgPackException("A primitive array's length is not a multiple of its element size.");
        }
        int count = bytes / size;
        List<T> values = new ArrayList<>(count);
        Out element = new Out();
        for (int i = 0; i < count; i++) {
            int p = 1 + i * size;
            long raw = 0;
            for (int k = size - 1; k >= 0; k--) {
                raw = (raw << 8) | (data[p + k] & 0xFF);
            }
            element.reset();
            switch (kind) {
                case 1:
                    element.integer((byte) raw);
                    break;
                case 2:
                    element.integer((short) raw);
                    break;
                case 3:
                case 10:
                case 5:
                    element.integer(raw);
                    break;
                case 4:
                    element.integer((int) raw);
                    break;
                case 6:
                    element.integer(raw);
                    break;
                case 7:
                    element.unsigned64(raw);
                    break;
                case 8:
                    element.float32(Float.intBitsToFloat((int) raw));
                    break;
                case 9:
                    element.float64(Double.longBitsToDouble(raw));
                    break;
                case 11:
                    element.float32(halfToFloat((int) raw));
                    break;
                default:
                    throw new MsgPackException("Unknown primitive array kind " + kind);
            }
            values.add(decodeValue(codec, itemFormatter, element.buffer, 0, element.size));
        }
        return values;
    }

    private static int elementSize(int kind) {
        switch (kind) {
            case 1:
                return 1;
            case 2:
            case 3:
            case 10:
            case 11:
                return 2;
            case 4:
            case 5:
            case 8:
                return 4;
            case 6:
            case 7:
            case 9:
                return 8;
            default:
                throw new MsgPackException("Unknown primitive array kind " + kind);
        }
    }

    /** IEEE 754 binary16 to binary32 ({@code Float.float16ToFloat} needs Java 20). */
    static float halfToFloat(int bits) {
        int sign = (bits & 0x8000) << 16;
        int exponent = (bits >>> 10) & 0x1F;
        int mantissa = bits & 0x3FF;
        if (exponent == 0x1F) {
            return Float.intBitsToFloat(sign | 0x7F800000 | (mantissa << 13));
        }
        if (exponent == 0) {
            if (mantissa == 0) {
                return Float.intBitsToFloat(sign);
            }
            // Subnormal: normalize.
            exponent = 1;
            while ((mantissa & 0x400) == 0) {
                mantissa <<= 1;
                exponent--;
            }
            mantissa &= 0x3FF;
        }
        return Float.intBitsToFloat(sign | ((exponent + 112) << 23) | (mantissa << 13));
    }

    // ------------------------------------------------------------------ encoding (client requests)

    /** {@code [pipeId: uint8]}. */
    static byte[] pipeArguments(int initialPipeId) {
        Out out = new Out();
        out.arrayHeader(1);
        out.integer(initialPipeId);
        return out.toArray();
    }

    /** Insert or Replace: {@code [tag, [0, version, index, value]]}. */
    static <T> byte[] valueRequest(int tag, int version, int index, @Nullable T value, PayloadCodec codec,
                                   Formatter<T> itemFormatter) {
        byte[] encoded = codec.writeValue(itemFormatter, value);
        Out out = header(tag, 4);
        out.integer(version);
        out.integer(index);
        out.raw(encoded.length == 0 ? new byte[] {(byte) 0xC0} : encoded);
        return out.toArray();
    }

    /** Move: {@code [6, [0, version, fromIndex, toIndex]]}. */
    static byte[] moveRequest(int version, int fromIndex, int toIndex) {
        Out out = header(MOVE, 4);
        out.integer(version);
        out.integer(fromIndex);
        out.integer(toIndex);
        return out.toArray();
    }

    /** Remove: {@code [7, [0, version, index]]}. */
    static byte[] removeRequest(int version, int index) {
        Out out = header(REMOVE, 3);
        out.integer(version);
        out.integer(index);
        return out.toArray();
    }

    /** Clear: {@code [3, [0, version]]}. */
    static byte[] clearRequest(int version) {
        Out out = header(CLEAR, 2);
        out.integer(version);
        return out.toArray();
    }

    private static Out header(int tag, int fields) {
        Out out = new Out();
        out.arrayHeader(2);
        out.integer(tag);
        out.arrayHeader(fields);
        // A client sends every request with flags 0 (specification section 12.3).
        out.integer(0);
        return out;
    }

    // ------------------------------------------------------------------ MessagePack primitives

    /** A minimal MessagePack reader for the item envelopes. Malformed input throws {@link MsgPackException}. */
    static final class In {
        final byte[] data;
        int position;
        int extLength;

        In(byte[] data) {
            this.data = data;
        }

        int remaining() {
            return data.length - position;
        }

        int peek() {
            need(1);
            return data[position] & 0xFF;
        }

        private void need(long n) {
            if (n > remaining()) {
                throw new MsgPackException("A collection item is truncated.");
            }
        }

        private int u8() {
            need(1);
            return data[position++] & 0xFF;
        }

        private long be(int bytes) {
            need(bytes);
            long value = 0;
            for (int i = 0; i < bytes; i++) {
                value = (value << 8) | (data[position++] & 0xFF);
            }
            return value;
        }

        int arrayHeader() {
            int b = u8();
            if ((b & 0xF0) == 0x90) {
                return b & 0x0F;
            }
            if (b == 0xDC) {
                return (int) be(2);
            }
            if (b == 0xDD) {
                long count = be(4);
                if (count > Integer.MAX_VALUE) {
                    throw new MsgPackException("Array too long.");
                }
                return (int) count;
            }
            throw new MsgPackException("Expected an array, found 0x" + Integer.toHexString(b));
        }

        /** Any integer encoding; uint64 above {@code Long.MAX_VALUE} is rejected. */
        long integer() {
            int b = u8();
            if (b <= 0x7F) {
                return b;
            }
            if (b >= 0xE0) {
                return (byte) b;
            }
            switch (b) {
                case 0xCC:
                    return be(1);
                case 0xCD:
                    return be(2);
                case 0xCE:
                    return be(4);
                case 0xCF: {
                    long value = be(8);
                    if (value < 0) {
                        throw new MsgPackException("Integer out of range.");
                    }
                    return value;
                }
                case 0xD0:
                    return (byte) be(1);
                case 0xD1:
                    return (short) be(2);
                case 0xD2:
                    return (int) be(4);
                case 0xD3:
                    return be(8);
                default:
                    throw new MsgPackException("Expected an integer, found 0x" + Integer.toHexString(b));
            }
        }

        int int32() {
            long value = integer();
            if (value < Integer.MIN_VALUE || value > Integer.MAX_VALUE) {
                throw new MsgPackException("Integer out of int32 range: " + value);
            }
            return (int) value;
        }

        int int32Range(int min, int max, String what) {
            long value = integer();
            if (value < min || value > max) {
                throw new MsgPackException("The " + what + " are out of range: " + value);
            }
            return (int) value;
        }

        byte[] binary() {
            int b = u8();
            long length;
            switch (b) {
                case 0xC4:
                    length = be(1);
                    break;
                case 0xC5:
                    length = be(2);
                    break;
                case 0xC6:
                    length = be(4);
                    break;
                default:
                    throw new MsgPackException("Expected bin, found 0x" + Integer.toHexString(b));
            }
            return raw(length);
        }

        /** Reads an ext header whose first byte is {@code b}; sets {@link #extLength}, returns the type. */
        int extHeaderType(int b) {
            position++;
            long length;
            switch (b) {
                case 0xD4:
                    length = 1;
                    break;
                case 0xD5:
                    length = 2;
                    break;
                case 0xD6:
                    length = 4;
                    break;
                case 0xD7:
                    length = 8;
                    break;
                case 0xD8:
                    length = 16;
                    break;
                case 0xC7:
                    length = be(1);
                    break;
                case 0xC8:
                    length = be(2);
                    break;
                case 0xC9:
                    length = be(4);
                    break;
                default:
                    throw new MsgPackException("Expected a value array, found 0x" + Integer.toHexString(b));
            }
            int type = (byte) u8();
            need(length);
            extLength = (int) length;
            return type;
        }

        byte[] raw(long length) {
            need(length);
            byte[] bytes = Arrays.copyOfRange(data, position, position + (int) length);
            position += (int) length;
            return bytes;
        }

        /** Skips one value. */
        void skip() {
            long pending = 1;
            while (pending > 0) {
                int t = u8();
                pending--;
                if (t <= 0x7F || t >= 0xE0) {
                    continue;
                }
                if (t <= 0x8F) {
                    pending += 2L * (t & 0x0F);
                    continue;
                }
                if (t <= 0x9F) {
                    pending += t & 0x0F;
                    continue;
                }
                if (t <= 0xBF) {
                    skipBytes(t & 0x1F);
                    continue;
                }
                switch (t) {
                    case 0xC0:
                    case 0xC2:
                    case 0xC3:
                        break;
                    case 0xC4:
                    case 0xD9:
                        skipBytes(be(1));
                        break;
                    case 0xC5:
                    case 0xDA:
                        skipBytes(be(2));
                        break;
                    case 0xC6:
                    case 0xDB:
                        skipBytes(be(4));
                        break;
                    case 0xC7:
                        skipBytes(be(1) + 1);
                        break;
                    case 0xC8:
                        skipBytes(be(2) + 1);
                        break;
                    case 0xC9:
                        skipBytes(be(4) + 1);
                        break;
                    case 0xCC:
                    case 0xD0:
                        skipBytes(1);
                        break;
                    case 0xCD:
                    case 0xD1:
                        skipBytes(2);
                        break;
                    case 0xCA:
                    case 0xCE:
                    case 0xD2:
                        skipBytes(4);
                        break;
                    case 0xCB:
                    case 0xCF:
                    case 0xD3:
                        skipBytes(8);
                        break;
                    case 0xD4:
                        skipBytes(2);
                        break;
                    case 0xD5:
                        skipBytes(3);
                        break;
                    case 0xD6:
                        skipBytes(5);
                        break;
                    case 0xD7:
                        skipBytes(9);
                        break;
                    case 0xD8:
                        skipBytes(17);
                        break;
                    case 0xDC:
                        pending += be(2);
                        break;
                    case 0xDD:
                        pending += be(4);
                        break;
                    case 0xDE:
                        pending += 2 * be(2);
                        break;
                    case 0xDF:
                        pending += 2 * be(4);
                        break;
                    default:
                        throw new MsgPackException("Invalid MessagePack byte 0x" + Integer.toHexString(t));
                }
                if (pending > remaining()) {
                    throw new MsgPackException("A collection item is truncated.");
                }
            }
        }

        private void skipBytes(long n) {
            need(n);
            position += (int) n;
        }
    }

    /** A minimal canonical MessagePack writer (smallest form, as in specification section 10.1). */
    static final class Out {
        byte[] buffer = new byte[32];
        int size;

        void reset() {
            size = 0;
        }

        private void ensure(int n) {
            if (size + n > buffer.length) {
                buffer = Arrays.copyOf(buffer, Math.max(buffer.length * 2, size + n));
            }
        }

        private void put(int b) {
            ensure(1);
            buffer[size++] = (byte) b;
        }

        private void putBe(long value, int bytes) {
            ensure(bytes);
            for (int i = bytes - 1; i >= 0; i--) {
                buffer[size++] = (byte) (value >>> (8 * i));
            }
        }

        void arrayHeader(int count) {
            if (count < 16) {
                put(0x90 | count);
            } else if (count <= 0xFFFF) {
                put(0xDC);
                putBe(count, 2);
            } else {
                put(0xDD);
                putBe(count, 4);
            }
        }

        void integer(long value) {
            if (value >= 0) {
                if (value <= 0x7F) {
                    put((int) value);
                } else if (value <= 0xFF) {
                    put(0xCC);
                    putBe(value, 1);
                } else if (value <= 0xFFFF) {
                    put(0xCD);
                    putBe(value, 2);
                } else if (value <= 0xFFFFFFFFL) {
                    put(0xCE);
                    putBe(value, 4);
                } else {
                    put(0xCF);
                    putBe(value, 8);
                }
            } else if (value >= -32) {
                put((int) value & 0xFF);
            } else if (value >= Byte.MIN_VALUE) {
                put(0xD0);
                putBe(value, 1);
            } else if (value >= Short.MIN_VALUE) {
                put(0xD1);
                putBe(value, 2);
            } else if (value >= Integer.MIN_VALUE) {
                put(0xD2);
                putBe(value, 4);
            } else {
                put(0xD3);
                putBe(value, 8);
            }
        }

        /** An unsigned 64-bit value held in a long. */
        void unsigned64(long value) {
            if (value < 0) {
                put(0xCF);
                putBe(value, 8);
            } else {
                integer(value);
            }
        }

        void float32(float value) {
            put(0xCA);
            putBe(Float.floatToRawIntBits(value) & 0xFFFFFFFFL, 4);
        }

        void float64(double value) {
            put(0xCB);
            putBe(Double.doubleToRawLongBits(value), 8);
        }

        void raw(byte[] bytes) {
            ensure(bytes.length);
            System.arraycopy(bytes, 0, buffer, size, bytes.length);
            size += bytes.length;
        }

        byte[] toArray() {
            return Arrays.copyOf(buffer, size);
        }
    }
}
