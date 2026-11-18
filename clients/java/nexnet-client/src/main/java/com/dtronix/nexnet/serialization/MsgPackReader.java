package com.dtronix.nexnet.serialization;

import org.jspecify.annotations.Nullable;

/**
 * Reads MessagePack from a byte range. Not thread-safe. Enforces the hardening limits of the C# reader
 * (maximum depth, maximum element count, strict UTF-8).
 *
 * <p>Skeleton (K01); implemented by K02, which may add methods but not change these.
 */
public final class MsgPackReader {
    /**
     * Creates a reader over part of an array. The array is not copied.
     *
     * @param buffer the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public MsgPackReader(byte[] buffer, int offset, int length) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The type family of the next value, without consuming it.
     *
     * @return the type
     * @throws MsgPackException at the end of the input
     */
    public MsgPackType peekType() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Consumes a nil if one is next.
     *
     * @return true when a nil was consumed
     */
    public boolean tryReadNil() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads a boolean.
     *
     * @return the value
     */
    public boolean readBoolean() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads an integer that fits in a {@code long}.
     *
     * @return the value
     * @throws MsgPackException when the value is not an integer or does not fit
     */
    public long readLong() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads an integer that fits in an {@code int}.
     *
     * @return the value
     * @throws MsgPackException when the value is not an integer or does not fit
     */
    public int readInt() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads an unsigned 64-bit integer (C# {@code ulong}) as raw bits.
     *
     * @return the raw bits
     */
    public long readUInt64() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads a float 32 (or a narrower numeric encoding).
     *
     * @return the value
     */
    public float readFloat() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads a float 64 (or a narrower numeric encoding).
     *
     * @return the value
     */
    public double readDouble() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads a string, or null for nil.
     *
     * @return the value
     */
    public @Nullable String readString() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads bin, or null for nil.
     *
     * @return a new array
     */
    public byte @Nullable [] readBinary() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads an array header.
     *
     * @return the number of elements
     */
    public int readArrayHeader() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads a map header.
     *
     * @return the number of key/value pairs
     */
    public int readMapHeader() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads an ext header; the payload follows and is read with {@link #readRaw(int)}.
     *
     * @return the payload length; the type code is available from {@link #lastExtType()}
     */
    public int readExtHeader() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The type code of the last ext header read.
     *
     * @return the type code
     */
    public int lastExtType() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Reads bytes as they are.
     *
     * @param length the number of bytes
     * @return a new array
     */
    public byte[] readRaw(int length) {
        throw new UnsupportedOperationException("not implemented");
    }

    /** Skips the next value, including nested arrays and maps. */
    public void skip() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The number of unread bytes.
     *
     * @return the remaining length
     */
    public int remaining() {
        throw new UnsupportedOperationException("not implemented");
    }
}
