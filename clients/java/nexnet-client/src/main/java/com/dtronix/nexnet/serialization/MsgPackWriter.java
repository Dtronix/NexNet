package com.dtronix.nexnet.serialization;

import java.util.Arrays;
import org.jspecify.annotations.Nullable;

/**
 * Writes MessagePack (big-endian, smallest canonical encoding) into a growable buffer. Not thread-safe.
 *
 * <p>The encodings are those of the C# writer: non-negative integers use the unsigned family (positive fixint,
 * uint8..uint64) and negative ones the signed family; {@code float} is always float 32 and {@code double} always
 * float 64, with their exact bits (NaN payloads included); bin uses bin 8 even for short data; ext headers use the
 * fixext forms when the length allows. Strings are UTF-8, an unpaired surrogate written as U+FFFD.
 *
 * <p>The buffer comes from a small shared pool. {@link #close()} gives it back; a writer that is not closed is simply
 * garbage collected.
 */
public final class MsgPackWriter implements AutoCloseable {
    private static final byte[] EMPTY = new byte[0];

    private byte[] buffer;
    private int size;

    /** Creates a writer with an empty buffer. */
    public MsgPackWriter() {
        buffer = EMPTY;
    }

    // ------------------------------------------------------------------ Buffer

    /** Makes room for {@code count} more bytes. */
    void ensure(int count) {
        int needed = size + count;
        if (needed < 0) {
            throw new MsgPackException(MsgPackException.Kind.SIZE, "The value is larger than 2 GiB.");
        }
        if (needed <= buffer.length) {
            return;
        }
        byte[] old = buffer;
        int grown = old.length == 0 ? BufferPool.INITIAL_SIZE : (int) Math.min(Integer.MAX_VALUE - 8L, old.length * 2L);
        byte[] next = BufferPool.rent(Math.max(needed, grown));
        System.arraycopy(old, 0, next, 0, size);
        buffer = next;
        if (old != EMPTY) {
            BufferPool.giveBack(old);
        }
    }

    /** The internal buffer; valid until the next write. */
    byte[] buffer() {
        return buffer;
    }

    /** Marks {@code count} bytes written directly into {@link #buffer()} after {@link #ensure(int)}. */
    void advance(int count) {
        size += count;
    }

    private void put(int b) {
        ensure(1);
        buffer[size++] = (byte) b;
    }

    private void put2(int code, int b) {
        ensure(2);
        buffer[size++] = (byte) code;
        buffer[size++] = (byte) b;
    }

    private void put16(int code, int v) {
        ensure(3);
        byte[] a = buffer;
        int p = size;
        a[p] = (byte) code;
        a[p + 1] = (byte) (v >>> 8);
        a[p + 2] = (byte) v;
        size = p + 3;
    }

    private void put32(int code, int v) {
        ensure(5);
        byte[] a = buffer;
        int p = size;
        a[p] = (byte) code;
        a[p + 1] = (byte) (v >>> 24);
        a[p + 2] = (byte) (v >>> 16);
        a[p + 3] = (byte) (v >>> 8);
        a[p + 4] = (byte) v;
        size = p + 5;
    }

    private void put64(int code, long v) {
        ensure(9);
        byte[] a = buffer;
        int p = size;
        a[p] = (byte) code;
        for (int i = 8; i >= 1; i--) {
            a[p + i] = (byte) v;
            v >>>= 8;
        }
        size = p + 9;
    }

    // ------------------------------------------------------------------ Scalars

    /** Writes nil. */
    public void writeNil() {
        put(0xC0);
    }

    /**
     * Writes a boolean.
     *
     * @param value the value
     */
    public void writeBoolean(boolean value) {
        put(value ? 0xC3 : 0xC2);
    }

    /**
     * Writes a signed integer in its smallest encoding.
     *
     * @param value the value
     */
    public void writeInt(long value) {
        if (value >= 0) {
            writeNonNegative(value);
        } else if (value >= -32) {
            put((int) value & 0xFF);
        } else if (value >= Byte.MIN_VALUE) {
            put2(0xD0, (int) value);
        } else if (value >= Short.MIN_VALUE) {
            put16(0xD1, (int) value);
        } else if (value >= Integer.MIN_VALUE) {
            put32(0xD2, (int) value);
        } else {
            put64(0xD3, value);
        }
    }

    private void writeNonNegative(long value) {
        if (value <= 0x7F) {
            put((int) value);
        } else if (value <= 0xFF) {
            put2(0xCC, (int) value);
        } else if (value <= 0xFFFF) {
            put16(0xCD, (int) value);
        } else if (value <= 0xFFFF_FFFFL) {
            put32(0xCE, (int) value);
        } else {
            put64(0xCF, value);
        }
    }

    /**
     * Writes an unsigned 64-bit integer given as raw bits (C# {@code ulong}).
     *
     * @param value the raw bits
     */
    public void writeUInt64(long value) {
        if (value >= 0) {
            writeNonNegative(value);
        } else {
            put64(0xCF, value);
        }
    }

    /**
     * Writes a float 32 with the exact bits of the value.
     *
     * @param value the value
     */
    public void writeFloat(float value) {
        put32(0xCA, Float.floatToRawIntBits(value));
    }

    /**
     * Writes a float 64 with the exact bits of the value.
     *
     * @param value the value
     */
    public void writeDouble(double value) {
        put64(0xCB, Double.doubleToRawLongBits(value));
    }

    // ------------------------------------------------------------------ Strings and binary

    /**
     * Writes a UTF-8 string, or nil for null.
     *
     * @param value the value
     */
    public void writeString(@Nullable String value) {
        if (value == null) {
            writeNil();
            return;
        }
        int length = Utf8.length(value);
        writeStringHeader(length);
        ensure(length);
        size = Utf8.encode(value, buffer, size);
    }

    /**
     * Writes a str header for {@code byteLength} UTF-8 bytes, which follow via {@link #writeRaw(byte[], int, int)}.
     *
     * @param byteLength the UTF-8 length
     */
    public void writeStringHeader(int byteLength) {
        checkLength(byteLength);
        if (byteLength <= 31) {
            put(0xA0 | byteLength);
        } else if (byteLength <= 0xFF) {
            put2(0xD9, byteLength);
        } else if (byteLength <= 0xFFFF) {
            put16(0xDA, byteLength);
        } else {
            put32(0xDB, byteLength);
        }
    }

    /**
     * Writes bin, or nil for null.
     *
     * @param value the bytes
     */
    public void writeBinary(byte @Nullable [] value) {
        if (value == null) {
            writeNil();
            return;
        }
        writeBinary(value, 0, value.length);
    }

    /**
     * Writes bin from part of an array.
     *
     * @param value the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public void writeBinary(byte[] value, int offset, int length) {
        checkRange(value, offset, length);
        writeBinaryHeader(length);
        writeRaw(value, offset, length);
    }

    /**
     * Writes a bin header (always bin 8 or larger) for {@code length} bytes, which follow via
     * {@link #writeRaw(byte[], int, int)}.
     *
     * @param length the number of bytes
     */
    public void writeBinaryHeader(int length) {
        checkLength(length);
        if (length <= 0xFF) {
            put2(0xC4, length);
        } else if (length <= 0xFFFF) {
            put16(0xC5, length);
        } else {
            put32(0xC6, length);
        }
    }

    // ------------------------------------------------------------------ Containers and ext

    /**
     * Writes an array header; the elements follow.
     *
     * @param count the number of elements
     */
    public void writeArrayHeader(int count) {
        checkLength(count);
        if (count <= 15) {
            put(0x90 | count);
        } else if (count <= 0xFFFF) {
            put16(0xDC, count);
        } else {
            put32(0xDD, count);
        }
    }

    /**
     * Writes a map header; the key/value pairs follow.
     *
     * @param count the number of pairs
     */
    public void writeMapHeader(int count) {
        checkLength(count);
        if (count <= 15) {
            put(0x80 | count);
        } else if (count <= 0xFFFF) {
            put16(0xDE, count);
        } else {
            put32(0xDF, count);
        }
    }

    /**
     * Writes an ext header in its smallest form; the payload follows via {@link #writeRaw(byte[], int, int)}.
     *
     * @param type the ext type code (-128 to 127)
     * @param length the payload length in bytes
     */
    public void writeExtHeader(int type, int length) {
        if (type < Byte.MIN_VALUE || type > Byte.MAX_VALUE) {
            throw new IllegalArgumentException("ext type out of range: " + type);
        }
        checkLength(length);
        int fix;
        switch (length) {
            case 1:
                fix = 0xD4;
                break;
            case 2:
                fix = 0xD5;
                break;
            case 4:
                fix = 0xD6;
                break;
            case 8:
                fix = 0xD7;
                break;
            case 16:
                fix = 0xD8;
                break;
            default:
                fix = 0;
                break;
        }
        if (fix != 0) {
            put2(fix, type);
        } else if (length <= 0xFF) {
            put2(0xC7, length);
            put(type);
        } else if (length <= 0xFFFF) {
            put16(0xC8, length);
            put(type);
        } else {
            put32(0xC9, length);
            put(type);
        }
    }

    /**
     * Writes bytes as they are, with no MessagePack header.
     *
     * @param bytes the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public void writeRaw(byte[] bytes, int offset, int length) {
        checkRange(bytes, offset, length);
        ensure(length);
        System.arraycopy(bytes, offset, buffer, size, length);
        size += length;
    }

    /**
     * Writes bytes as they are, with no MessagePack header: for example one complete MessagePack value.
     *
     * @param bytes the bytes
     */
    public void writeRaw(byte[] bytes) {
        writeRaw(bytes, 0, bytes.length);
    }

    // ------------------------------------------------------------------ Result

    /**
     * The number of bytes written so far.
     *
     * @return the size
     */
    public int size() {
        return size;
    }

    /**
     * Copies the bytes written so far.
     *
     * @return a new array
     */
    public byte[] toByteArray() {
        return Arrays.copyOf(buffer, size);
    }

    /** Discards what was written; the buffer is kept for reuse. */
    public void reset() {
        size = 0;
    }

    /** Discards what was written and gives the buffer back to the shared pool. The writer stays usable. */
    @Override
    public void close() {
        byte[] old = buffer;
        buffer = EMPTY;
        size = 0;
        if (old != EMPTY) {
            BufferPool.giveBack(old);
        }
    }

    private static void checkLength(int length) {
        if (length < 0) {
            throw new IllegalArgumentException("negative length: " + length);
        }
    }

    private static void checkRange(byte[] bytes, int offset, int length) {
        if (offset < 0 || length < 0 || offset > bytes.length - length) {
            throw new IndexOutOfBoundsException("offset " + offset + ", length " + length + ", array " + bytes.length);
        }
    }
}
