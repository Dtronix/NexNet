package com.dtronix.nexnet.serialization;

import org.jspecify.annotations.Nullable;

/**
 * Writes MessagePack (big-endian, smallest canonical encoding) into a growable buffer. Not thread-safe.
 *
 * <p>Skeleton (K01); implemented by K02, which may add methods but not change these.
 */
public final class MsgPackWriter {
    /** Creates a writer with an empty buffer. */
    public MsgPackWriter() {
    }

    /** Writes nil. */
    public void writeNil() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a boolean.
     *
     * @param value the value
     */
    public void writeBoolean(boolean value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a signed integer in its smallest encoding.
     *
     * @param value the value
     */
    public void writeInt(long value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes an unsigned 64-bit integer given as raw bits (C# {@code ulong}).
     *
     * @param value the raw bits
     */
    public void writeUInt64(long value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a float 32.
     *
     * @param value the value
     */
    public void writeFloat(float value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a float 64.
     *
     * @param value the value
     */
    public void writeDouble(double value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a UTF-8 string, or nil for null.
     *
     * @param value the value
     */
    public void writeString(@Nullable String value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes bin, or nil for null.
     *
     * @param value the bytes
     */
    public void writeBinary(byte @Nullable [] value) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes bin from part of an array.
     *
     * @param value the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public void writeBinary(byte[] value, int offset, int length) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes an array header; the elements follow.
     *
     * @param count the number of elements
     */
    public void writeArrayHeader(int count) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes a map header; the key/value pairs follow.
     *
     * @param count the number of pairs
     */
    public void writeMapHeader(int count) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes an ext header; the payload follows via {@link #writeRaw(byte[], int, int)}.
     *
     * @param type the ext type code
     * @param length the payload length in bytes
     */
    public void writeExtHeader(int type, int length) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Writes bytes as they are, with no MessagePack header.
     *
     * @param bytes the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public void writeRaw(byte[] bytes, int offset, int length) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * The number of bytes written so far.
     *
     * @return the size
     */
    public int size() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Copies the bytes written so far.
     *
     * @return a new array
     */
    public byte[] toByteArray() {
        throw new UnsupportedOperationException("not implemented");
    }
}
