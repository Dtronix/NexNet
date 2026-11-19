package com.dtronix.nexnet.session;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import org.jspecify.annotations.Nullable;

/**
 * The few MessagePack forms protocol message bodies need (arrays, integers, strings, bin, nil), in the canonical
 * (smallest) encoding the C# writer produces: non-negative integers use the unsigned family. Payload values use the
 * full codec ({@code com.dtronix.nexnet.serialization}); this one only builds session control bodies.
 */
final class WireWriter {
    private byte[] buffer;
    private int size;

    WireWriter(int capacity) {
        buffer = new byte[Math.max(16, capacity)];
    }

    int size() {
        return size;
    }

    byte[] toByteArray() {
        return Arrays.copyOf(buffer, size);
    }

    void writeArrayHeader(int count) {
        if (count < 0) {
            throw new IllegalArgumentException("count");
        }
        if (count <= 15) {
            put(0x90 | count);
        } else if (count <= 0xFFFF) {
            put(0xDC);
            put16(count);
        } else {
            put(0xDD);
            put32(count);
        }
    }

    void writeNil() {
        put(0xC0);
    }

    void writeInt(long value) {
        if (value >= 0) {
            if (value <= 0x7F) {
                put((int) value);
            } else if (value <= 0xFF) {
                put(0xCC);
                put((int) value);
            } else if (value <= 0xFFFF) {
                put(0xCD);
                put16((int) value);
            } else if (value <= 0xFFFFFFFFL) {
                put(0xCE);
                put32((int) value);
            } else {
                put(0xCF);
                put64(value);
            }
        } else if (value >= -32) {
            put((int) value & 0xFF);
        } else if (value >= Byte.MIN_VALUE) {
            put(0xD0);
            put((int) value);
        } else if (value >= Short.MIN_VALUE) {
            put(0xD1);
            put16((int) value);
        } else if (value >= Integer.MIN_VALUE) {
            put(0xD2);
            put32((int) value);
        } else {
            put(0xD3);
            put64(value);
        }
    }

    void writeString(@Nullable String value) {
        if (value == null) {
            writeNil();
            return;
        }
        byte[] utf8 = value.getBytes(StandardCharsets.UTF_8);
        int length = utf8.length;
        if (length <= 31) {
            put(0xA0 | length);
        } else if (length <= 0xFF) {
            put(0xD9);
            put(length);
        } else if (length <= 0xFFFF) {
            put(0xDA);
            put16(length);
        } else {
            put(0xDB);
            put32(length);
        }
        putBytes(utf8, 0, length);
    }

    void writeBinary(byte[] value) {
        int length = value.length;
        if (length <= 0xFF) {
            put(0xC4);
            put(length);
        } else if (length <= 0xFFFF) {
            put(0xC5);
            put16(length);
        } else {
            put(0xC6);
            put32(length);
        }
        putBytes(value, 0, length);
    }

    private void ensure(int extra) {
        if (size + extra > buffer.length) {
            buffer = Arrays.copyOf(buffer, Math.max(buffer.length * 2, size + extra));
        }
    }

    private void put(int b) {
        ensure(1);
        buffer[size++] = (byte) b;
    }

    private void put16(int v) {
        ensure(2);
        buffer[size++] = (byte) (v >>> 8);
        buffer[size++] = (byte) v;
    }

    private void put32(int v) {
        ensure(4);
        buffer[size++] = (byte) (v >>> 24);
        buffer[size++] = (byte) (v >>> 16);
        buffer[size++] = (byte) (v >>> 8);
        buffer[size++] = (byte) v;
    }

    private void put64(long v) {
        put32((int) (v >>> 32));
        put32((int) v);
    }

    private void putBytes(byte[] bytes, int offset, int length) {
        ensure(length);
        System.arraycopy(bytes, offset, buffer, size, length);
        size += length;
    }
}
