package com.dtronix.nexnet.invocation;

import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import org.jspecify.annotations.Nullable;

/**
 * Writes the MessagePack envelopes of invocation messages (protocol specification section 4) in the canonical, most
 * compact encoding. Payloads (arguments, results) are written by formatters and embedded with {@link #raw}.
 */
final class WireOut {
    private byte[] buffer;
    private int size;

    WireOut(int capacity) {
        buffer = new byte[Math.max(16, capacity)];
    }

    int size() {
        return size;
    }

    byte[] toByteArray() {
        return size == buffer.length ? buffer : Arrays.copyOf(buffer, size);
    }

    void arrayHeader(int count) {
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

    void nil() {
        put(0xC0);
    }

    /** A signed integer, compact. */
    void integer(long value) {
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
                put32((int) (value >>> 32));
                put32((int) value);
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
            put32((int) (value >>> 32));
            put32((int) value);
        }
    }

    void string(@Nullable String value) {
        if (value == null) {
            nil();
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
        raw(utf8, 0, length);
    }

    void raw(byte[] bytes, int offset, int length) {
        ensure(length);
        System.arraycopy(bytes, offset, buffer, size, length);
        size += length;
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
}
