package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackException;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

/**
 * An item formatter for tests that works on raw MessagePack bytes through {@link CollectionCodec}, so the collection
 * runtime can be tested before the MessagePack codec (K02) exists.
 *
 * @param <T> the item type
 */
abstract class TestFormat<T> implements Formatter<T> {
    /** Any MessagePack integer as a Long; uint64 above Long.MAX_VALUE keeps its bits. */
    static final TestFormat<Long> LONG = new TestFormat<Long>() {
        @Override
        Long decode(byte[] bytes) {
            if ((bytes[0] & 0xFF) == 0xCF) {
                long value = 0;
                for (int i = 1; i <= 8; i++) {
                    value = (value << 8) | (bytes[i] & 0xFF);
                }
                return value;
            }
            CollectionWire.In in = new CollectionWire.In(bytes);
            long value = in.integer();
            check(in);
            return value;
        }

        @Override
        byte[] encode(Long value) {
            CollectionWire.Out out = new CollectionWire.Out();
            out.integer(value);
            return out.toArray();
        }
    };

    /** float32 or float64 as a Double. */
    static final TestFormat<Double> DOUBLE = new TestFormat<Double>() {
        @Override
        Double decode(byte[] bytes) {
            int type = bytes[0] & 0xFF;
            long bits = 0;
            for (int i = 1; i < bytes.length; i++) {
                bits = (bits << 8) | (bytes[i] & 0xFF);
            }
            if (type == 0xCA && bytes.length == 5) {
                return (double) Float.intBitsToFloat((int) bits);
            }
            if (type == 0xCB && bytes.length == 9) {
                return Double.longBitsToDouble(bits);
            }
            throw new MsgPackException("Not a float: 0x" + Integer.toHexString(type));
        }

        @Override
        byte[] encode(Double value) {
            CollectionWire.Out out = new CollectionWire.Out();
            out.float64(value);
            return out.toArray();
        }
    };

    /** A nullable UTF-8 string. */
    static final TestFormat<String> STRING = new TestFormat<String>() {
        @Override
        String decode(byte[] bytes) {
            int type = bytes[0] & 0xFF;
            if (type == 0xC0 && bytes.length == 1) {
                return null;
            }
            int start;
            int length;
            if ((type & 0xE0) == 0xA0) {
                start = 1;
                length = type & 0x1F;
            } else if (type == 0xD9) {
                start = 2;
                length = bytes[1] & 0xFF;
            } else if (type == 0xDA) {
                start = 3;
                length = ((bytes[1] & 0xFF) << 8) | (bytes[2] & 0xFF);
            } else {
                throw new MsgPackException("Not a string: 0x" + Integer.toHexString(type));
            }
            if (start + length != bytes.length) {
                throw new MsgPackException("String length mismatch.");
            }
            return new String(bytes, start, length, StandardCharsets.UTF_8);
        }

        @Override
        byte[] encode(String value) {
            if (value == null) {
                return new byte[] {(byte) 0xC0};
            }
            byte[] utf8 = value.getBytes(StandardCharsets.UTF_8);
            if (utf8.length >= 32) {
                throw new IllegalArgumentException("Test strings are short.");
            }
            byte[] out = new byte[utf8.length + 1];
            out[0] = (byte) (0xA0 | utf8.length);
            System.arraycopy(utf8, 0, out, 1, utf8.length);
            return out;
        }
    };

    /** A bool. */
    static final TestFormat<Boolean> BOOL = new TestFormat<Boolean>() {
        @Override
        Boolean decode(byte[] bytes) {
            if (bytes.length == 1 && (bytes[0] & 0xFF) == 0xC3) {
                return true;
            }
            if (bytes.length == 1 && (bytes[0] & 0xFF) == 0xC2) {
                return false;
            }
            throw new MsgPackException("Not a bool.");
        }

        @Override
        byte[] encode(Boolean value) {
            return new byte[] {(byte) (value ? 0xC3 : 0xC2)};
        }
    };

    abstract T decode(byte[] bytes);

    abstract byte[] encode(T value);

    static void check(CollectionWire.In in) {
        if (in.remaining() != 0) {
            throw new MsgPackException("Trailing bytes.");
        }
    }

    @Override
    public void write(MsgPackWriter writer, T value) {
        throw new UnsupportedOperationException("Use CollectionCodec.");
    }

    @Override
    public T read(MsgPackReader reader) {
        throw new UnsupportedOperationException("Use CollectionCodec.");
    }

    static byte[] copy(byte[] buffer, int offset, int length) {
        return Arrays.copyOfRange(buffer, offset, offset + length);
    }
}
