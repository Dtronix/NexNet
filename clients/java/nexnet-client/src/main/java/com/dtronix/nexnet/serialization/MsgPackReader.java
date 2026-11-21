package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.serialization.MsgPackException.Kind;
import org.jspecify.annotations.Nullable;

/**
 * Reads MessagePack from a byte range. Not thread-safe. Enforces the hardening limits of the C# reader
 * ({@link MsgPackOptions}: maximum depth, maximum element count, strict UTF-8), and checks every declared length and
 * count against the remaining input before anything is allocated.
 *
 * <p>Integer reads accept every integer encoding (not only the canonical one) and fail with
 * {@link MsgPackException.Kind#RANGE} when the value does not fit. Float reads accept float 32, float 64 and integers.
 * Malformed input throws {@link MsgPackException}; its {@link MsgPackException#kind()} says why.
 *
 * <p>Formatters of containers (sequences, sets, maps, tuples, objects, unions) call {@link #enter()} after reading their
 * header and {@link #exit()} after their elements, which enforces {@link MsgPackOptions#maxDepth()}.
 */
public final class MsgPackReader {
    private final byte[] data;
    private final int start;
    private final int end;
    private final MsgPackOptions options;
    private int position;
    private int depth;
    private long elements;
    private int lastExtType;

    /**
     * Creates a reader over part of an array with the {@link MsgPackOptions#UNTRUSTED} options. The array is not
     * copied.
     *
     * @param buffer the bytes
     * @param offset the first byte
     * @param length the number of bytes
     */
    public MsgPackReader(byte[] buffer, int offset, int length) {
        this(buffer, offset, length, MsgPackOptions.UNTRUSTED);
    }

    /**
     * Creates a reader over part of an array. The array is not copied.
     *
     * @param buffer the bytes
     * @param offset the first byte
     * @param length the number of bytes
     * @param options the hardening options
     */
    public MsgPackReader(byte[] buffer, int offset, int length, MsgPackOptions options) {
        if (offset < 0 || length < 0 || offset > buffer.length - length) {
            throw new IndexOutOfBoundsException("offset " + offset + ", length " + length + ", array " + buffer.length);
        }
        if (options == null) {
            throw new NullPointerException("options");
        }
        this.data = buffer;
        this.start = offset;
        this.end = offset + length;
        this.position = offset;
        this.options = options;
    }

    /**
     * Creates a reader over a whole array with the {@link MsgPackOptions#UNTRUSTED} options.
     *
     * @param buffer the bytes
     */
    public MsgPackReader(byte[] buffer) {
        this(buffer, 0, buffer.length, MsgPackOptions.UNTRUSTED);
    }

    // ------------------------------------------------------------------ Cursor

    /**
     * The options in effect.
     *
     * @return the options
     */
    public MsgPackOptions options() {
        return options;
    }

    /**
     * The number of unread bytes.
     *
     * @return the remaining length
     */
    public int remaining() {
        return end - position;
    }

    /**
     * The number of bytes read so far.
     *
     * @return the consumed length
     */
    public int consumed() {
        return position - start;
    }

    /**
     * True when no bytes remain.
     *
     * @return whether the input is consumed
     */
    public boolean isEnd() {
        return position >= end;
    }

    /**
     * Fails when bytes remain: a value was read and nothing may follow it.
     *
     * @throws MsgPackException ({@link MsgPackException.Kind#TRAILING}) when bytes remain
     */
    public void ensureEnd() {
        if (position < end) {
            throw new MsgPackException(Kind.TRAILING, (end - position) + " bytes after the value.");
        }
    }

    /**
     * Enters a nested container (after its header is read).
     *
     * @throws MsgPackException ({@link MsgPackException.Kind#DEPTH}) beyond {@link MsgPackOptions#maxDepth()}
     */
    public void enter() {
        if (++depth > options.maxDepth()) {
            throw new MsgPackException(Kind.DEPTH, "Nesting deeper than " + options.maxDepth() + ".");
        }
    }

    /** Leaves a nested container. */
    public void exit() {
        depth--;
    }

    private int code() {
        if (position >= end) {
            throw truncated();
        }
        return data[position++] & 0xFF;
    }

    private void need(long count) {
        if (count > end - position) {
            throw truncated();
        }
    }

    private int u8() {
        need(1);
        return data[position++] & 0xFF;
    }

    private int u16() {
        need(2);
        int v = ((data[position] & 0xFF) << 8) | (data[position + 1] & 0xFF);
        position += 2;
        return v;
    }

    private int i32() {
        need(4);
        int p = position;
        int v = ((data[p] & 0xFF) << 24) | ((data[p + 1] & 0xFF) << 16) | ((data[p + 2] & 0xFF) << 8)
            | (data[p + 3] & 0xFF);
        position = p + 4;
        return v;
    }

    private long i64() {
        need(8);
        long v = 0;
        for (int i = 0; i < 8; i++) {
            v = (v << 8) | (data[position + i] & 0xFF);
        }
        position += 8;
        return v;
    }

    private static MsgPackException truncated() {
        return new MsgPackException(Kind.TRUNCATED, "The input ends inside the value.");
    }

    private static MsgPackException unexpected(int code, String expected) {
        if (code == 0xC1) {
            return new MsgPackException(Kind.RESERVED, "The reserved code 0xc1 is not a value.");
        }
        return new MsgPackException(Kind.TYPE, "Expected " + expected + ", found code 0x" + Integer.toHexString(code)
            + ".");
    }

    private static MsgPackException range(String type) {
        return new MsgPackException(Kind.RANGE, "The integer is outside the " + type + " range.");
    }

    private void checkLength(long length) {
        if (length > end - position) {
            throw new MsgPackException(Kind.LENGTH, "A declared length of " + length + " exceeds the " + (end - position)
                + " remaining bytes.");
        }
    }

    private void countElements(long count) {
        int max = options.maxElements();
        if (max > 0) {
            elements += count;
            if (elements > max) {
                throw new MsgPackException(Kind.ELEMENTS, "More than " + max + " elements.");
            }
        }
    }

    // ------------------------------------------------------------------ Type and nil

    /**
     * The type family of the next value, without consuming it.
     *
     * @return the type
     * @throws MsgPackException at the end of the input, or for the reserved code 0xc1
     */
    public MsgPackType peekType() {
        if (position >= end) {
            throw truncated();
        }
        int c = data[position] & 0xFF;
        if (c <= 0x7F || c >= 0xE0) {
            return MsgPackType.INTEGER;
        }
        if (c <= 0x8F) {
            return MsgPackType.MAP;
        }
        if (c <= 0x9F) {
            return MsgPackType.ARRAY;
        }
        if (c <= 0xBF) {
            return MsgPackType.STRING;
        }
        switch (c) {
            case 0xC0:
                return MsgPackType.NIL;
            case 0xC2:
            case 0xC3:
                return MsgPackType.BOOLEAN;
            case 0xC4:
            case 0xC5:
            case 0xC6:
                return MsgPackType.BINARY;
            case 0xC7:
            case 0xC8:
            case 0xC9:
            case 0xD4:
            case 0xD5:
            case 0xD6:
            case 0xD7:
            case 0xD8:
                return MsgPackType.EXTENSION;
            case 0xCA:
            case 0xCB:
                return MsgPackType.FLOAT;
            case 0xD9:
            case 0xDA:
            case 0xDB:
                return MsgPackType.STRING;
            case 0xDC:
            case 0xDD:
                return MsgPackType.ARRAY;
            case 0xDE:
            case 0xDF:
                return MsgPackType.MAP;
            case 0xC1:
                throw unexpected(c, "a value");
            default:
                return MsgPackType.INTEGER; // 0xcc..0xd3
        }
    }

    /**
     * The first byte of the next value (its MessagePack code), without consuming it.
     *
     * @return the code, 0 to 255
     * @throws MsgPackException at the end of the input
     */
    public int peekCode() {
        if (position >= end) {
            throw truncated();
        }
        return data[position] & 0xFF;
    }

    /**
     * True when the next value is nil (does not consume it). False at the end of the input.
     *
     * @return whether nil is next
     */
    public boolean isNil() {
        return position < end && data[position] == (byte) 0xC0;
    }

    /**
     * Consumes a nil if one is next.
     *
     * @return true when a nil was consumed
     */
    public boolean tryReadNil() {
        if (isNil()) {
            position++;
            return true;
        }
        return false;
    }

    /**
     * Reads a nil.
     *
     * @throws MsgPackException when the next value is not nil
     */
    public void readNil() {
        int c = code();
        if (c != 0xC0) {
            throw unexpected(c, "nil");
        }
    }

    /**
     * Reads a boolean.
     *
     * @return the value
     */
    public boolean readBoolean() {
        int c = code();
        if (c == 0xC3) {
            return true;
        }
        if (c == 0xC2) {
            return false;
        }
        throw unexpected(c, "a boolean");
    }

    // ------------------------------------------------------------------ Integers

    /**
     * Reads an integer that fits in a {@code long}.
     *
     * @return the value
     * @throws MsgPackException when the value is not an integer or does not fit
     */
    public long readLong() {
        int c = code();
        if (c <= 0x7F) {
            return c;
        }
        if (c >= 0xE0) {
            return (byte) c;
        }
        switch (c) {
            case 0xCC:
                return u8();
            case 0xCD:
                return u16();
            case 0xCE:
                return i32() & 0xFFFF_FFFFL;
            case 0xCF: {
                long v = i64();
                if (v < 0) {
                    throw range("Int64");
                }
                return v;
            }
            case 0xD0:
                return (byte) u8();
            case 0xD1:
                return (short) u16();
            case 0xD2:
                return i32();
            case 0xD3:
                return i64();
            default:
                throw unexpected(c, "an integer");
        }
    }

    /**
     * Reads an unsigned 64-bit integer (C# {@code ulong}) as raw bits.
     *
     * @return the raw bits
     * @throws MsgPackException when the value is not an integer or is negative
     */
    public long readUInt64() {
        int c = code();
        if (c <= 0x7F) {
            return c;
        }
        long signed;
        switch (c) {
            case 0xCC:
                return u8();
            case 0xCD:
                return u16();
            case 0xCE:
                return i32() & 0xFFFF_FFFFL;
            case 0xCF:
                return i64();
            case 0xD0:
                signed = (byte) u8();
                break;
            case 0xD1:
                signed = (short) u16();
                break;
            case 0xD2:
                signed = i32();
                break;
            case 0xD3:
                signed = i64();
                break;
            default:
                if (c >= 0xE0) {
                    throw range("UInt64");
                }
                throw unexpected(c, "an integer");
        }
        if (signed < 0) {
            throw range("UInt64");
        }
        return signed;
    }

    private long readInRange(long min, long max, String type) {
        long v = readLong();
        if (v < min || v > max) {
            throw range(type);
        }
        return v;
    }

    /**
     * Reads an integer that fits in an {@code int}.
     *
     * @return the value
     * @throws MsgPackException when the value is not an integer or does not fit
     */
    public int readInt() {
        if (position < end) {
            int c = data[position] & 0xFF;
            if (c <= 0x7F) {
                position++;
                return c;
            }
            if (c >= 0xE0) {
                position++;
                return (byte) c;
            }
        }
        return (int) readInRange(Integer.MIN_VALUE, Integer.MAX_VALUE, "Int32");
    }

    /**
     * Reads a C# {@code uint} (0 to 4294967295).
     *
     * @return the value
     */
    public long readUInt32() {
        long v = readUInt64();
        if (v < 0 || v > 0xFFFF_FFFFL) {
            throw range("UInt32");
        }
        return v;
    }

    /**
     * Reads a C# {@code short}.
     *
     * @return the value
     */
    public short readShort() {
        return (short) readInRange(Short.MIN_VALUE, Short.MAX_VALUE, "Int16");
    }

    /**
     * Reads a C# {@code ushort} (0 to 65535).
     *
     * @return the value
     */
    public int readUInt16() {
        if (position < end && (data[position] & 0xFF) <= 0x7F) {
            return data[position++];
        }
        long v = readUInt64();
        if (v < 0 || v > 0xFFFF) {
            throw range("UInt16");
        }
        return (int) v;
    }

    /**
     * Reads a C# {@code sbyte} (-128 to 127).
     *
     * @return the value
     */
    public byte readSByte() {
        return (byte) readInRange(Byte.MIN_VALUE, Byte.MAX_VALUE, "SByte");
    }

    /**
     * Reads a C# {@code byte} (0 to 255).
     *
     * @return the value
     */
    public int readUInt8() {
        if (position < end && (data[position] & 0xFF) <= 0x7F) {
            return data[position++];
        }
        long v = readUInt64();
        if (v < 0 || v > 0xFF) {
            throw range("Byte");
        }
        return (int) v;
    }

    /**
     * Reads a C# {@code char} (a UTF-16 code unit written as an unsigned integer).
     *
     * @return the value
     */
    public char readChar() {
        return (char) readUInt16();
    }

    // ------------------------------------------------------------------ Floating point

    /**
     * Reads a float 32 (or a float 64, rounded, or an integer).
     *
     * @return the value
     */
    public float readFloat() {
        int c = peekCode();
        switch (c) {
            case 0xCA:
                position++;
                return Float.intBitsToFloat(i32());
            case 0xCB:
                position++;
                return (float) Double.longBitsToDouble(i64());
            case 0xCF:
                return unsignedToFloat(readUInt64());
            default:
                return readLong();
        }
    }

    /**
     * Reads a float 64 (or a float 32, or an integer).
     *
     * @return the value
     */
    public double readDouble() {
        int c = peekCode();
        switch (c) {
            case 0xCB:
                position++;
                return Double.longBitsToDouble(i64());
            case 0xCA:
                position++;
                return Float.intBitsToFloat(i32());
            case 0xCF:
                return unsignedToDouble(readUInt64());
            default:
                return readLong();
        }
    }

    /**
     * Reads a C# {@code Half}: a float rounded to the nearest binary16 value (ties to even), returned as the
     * {@code float} of that value.
     *
     * @return the value
     */
    public float readHalf() {
        return Float16.toFloat(Float16.fromFloat(readFloat()));
    }

    private static float unsignedToFloat(long bits) {
        if (bits >= 0) {
            return bits;
        }
        // Halve with the low bit kept sticky so the rounding of the conversion stays correct.
        return ((float) ((bits >>> 1) | (bits & 1))) * 2f;
    }

    private static double unsignedToDouble(long bits) {
        if (bits >= 0) {
            return bits;
        }
        return ((double) ((bits >>> 1) | (bits & 1))) * 2d;
    }

    // ------------------------------------------------------------------ Strings and binary

    /**
     * Reads a string, or null for nil.
     *
     * @return the value
     * @throws MsgPackException for another type, a length beyond the input or invalid UTF-8 (strict options)
     */
    public @Nullable String readString() {
        int c = code();
        int length;
        if (c >= 0xA0 && c <= 0xBF) {
            length = c & 0x1F;
        } else if (c == 0xC0) {
            return null;
        } else if (c == 0xD9) {
            length = u8();
        } else if (c == 0xDA) {
            length = u16();
        } else if (c == 0xDB) {
            length = checkedLength(i32() & 0xFFFF_FFFFL);
        } else {
            throw unexpected(c, "a string");
        }
        checkLength(length);
        String s = Utf8.decode(data, position, length, options.strictUtf8());
        position += length;
        return s;
    }

    /**
     * Reads the header of a bin value, or of a str value read as bytes (older writers used str for bytes).
     *
     * @return the length in bytes, or -1 for nil
     * @throws MsgPackException for another type or a length beyond the input
     */
    public int readBinaryHeader() {
        int c = code();
        long length;
        switch (c) {
            case 0xC0:
                return -1;
            case 0xC4:
            case 0xD9:
                length = u8();
                break;
            case 0xC5:
            case 0xDA:
                length = u16();
                break;
            case 0xC6:
            case 0xDB:
                length = i32() & 0xFFFF_FFFFL;
                break;
            default:
                if (c >= 0xA0 && c <= 0xBF) {
                    length = c & 0x1F;
                    break;
                }
                throw unexpected(c, "binary");
        }
        checkLength(length);
        return (int) length;
    }

    /**
     * Reads bin (or str bytes), or null for nil.
     *
     * @return a new array
     */
    public byte @Nullable [] readBinary() {
        int length = readBinaryHeader();
        if (length < 0) {
            return null;
        }
        return readRaw(length);
    }

    private int checkedLength(long length) {
        checkLength(length);
        return (int) length;
    }

    // ------------------------------------------------------------------ Containers and ext

    /**
     * Reads an array header. The count is checked against the remaining input (each element needs at least one
     * byte) and added to the element count of {@link MsgPackOptions#maxElements()}.
     *
     * @return the number of elements
     */
    public int readArrayHeader() {
        int c = code();
        long count;
        if (c >= 0x90 && c <= 0x9F) {
            count = c & 0x0F;
        } else if (c == 0xDC) {
            count = u16();
        } else if (c == 0xDD) {
            count = i32() & 0xFFFF_FFFFL;
        } else {
            throw unexpected(c, "an array");
        }
        checkLength(count);
        countElements(count);
        return (int) count;
    }

    /**
     * Reads an array header and requires exactly {@code expected} elements (tuples, unions, fixed shapes).
     *
     * @param expected the element count
     * @throws MsgPackException ({@link MsgPackException.Kind#COUNT}) for another count
     */
    public void readArrayHeader(int expected) {
        int count = readArrayHeader();
        if (count != expected) {
            throw new MsgPackException(Kind.COUNT, "Expected an array of " + expected + " elements, found " + count
                + ".");
        }
    }

    /**
     * Reads a map header. The pair count is checked against the remaining input.
     *
     * @return the number of key/value pairs
     */
    public int readMapHeader() {
        int c = code();
        long count;
        if (c >= 0x80 && c <= 0x8F) {
            count = c & 0x0F;
        } else if (c == 0xDE) {
            count = u16();
        } else if (c == 0xDF) {
            count = i32() & 0xFFFF_FFFFL;
        } else {
            throw unexpected(c, "a map");
        }
        checkLength(count * 2);
        countElements(count * 2);
        return (int) count;
    }

    /**
     * Reads an ext header; the payload follows and is read with {@link #readRaw(int)}.
     *
     * @return the payload length; the type code is available from {@link #lastExtType()}
     */
    public int readExtHeader() {
        int c = code();
        long length;
        switch (c) {
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
                length = u8();
                break;
            case 0xC8:
                length = u16();
                break;
            case 0xC9:
                length = i32() & 0xFFFF_FFFFL;
                break;
            default:
                throw unexpected(c, "an ext");
        }
        lastExtType = (byte) u8();
        checkLength(length);
        return (int) length;
    }

    /**
     * The type code of the last ext header read (-128 to 127).
     *
     * @return the type code
     */
    public int lastExtType() {
        return lastExtType;
    }

    /**
     * Reads bytes as they are.
     *
     * @param length the number of bytes
     * @return a new array
     */
    public byte[] readRaw(int length) {
        if (length < 0) {
            throw new IllegalArgumentException("negative length: " + length);
        }
        checkLength(length);
        byte[] result = new byte[length];
        System.arraycopy(data, position, result, 0, length);
        position += length;
        return result;
    }

    /** The underlying array, for codecs of this package that read in place. */
    byte[] array() {
        return data;
    }

    /** Consumes {@code length} bytes (checked against the input) and returns the array index of the first. */
    int take(int length) {
        checkLength(length);
        int at = position;
        position += length;
        return at;
    }

    /**
     * Consumes one complete value and returns its bytes (for embedded values carried as raw MessagePack).
     *
     * @return a new array with the value's bytes
     */
    public byte[] readRawValue() {
        int from = position;
        skip();
        byte[] result = new byte[position - from];
        System.arraycopy(data, from, result, 0, result.length);
        return result;
    }

    /** Skips the next value, including nested arrays and maps. Iterative: deep nesting costs no stack. */
    public void skip() {
        long pending = 1;
        while (pending > 0) {
            int c = code();
            pending--;
            long skipBytes;
            if (c <= 0x7F || c >= 0xE0 || c == 0xC0 || c == 0xC2 || c == 0xC3) {
                continue;
            } else if (c <= 0x8F) {
                pending += 2L * (c & 0x0F);
                continue;
            } else if (c <= 0x9F) {
                pending += c & 0x0F;
                continue;
            } else if (c <= 0xBF) {
                skipBytes = c & 0x1F;
            } else {
                switch (c) {
                    case 0xCC:
                    case 0xD0:
                        skipBytes = 1;
                        break;
                    case 0xCD:
                    case 0xD1:
                        skipBytes = 2;
                        break;
                    case 0xCA:
                    case 0xCE:
                    case 0xD2:
                        skipBytes = 4;
                        break;
                    case 0xCB:
                    case 0xCF:
                    case 0xD3:
                        skipBytes = 8;
                        break;
                    case 0xD4:
                        skipBytes = 2;
                        break;
                    case 0xD5:
                        skipBytes = 3;
                        break;
                    case 0xD6:
                        skipBytes = 5;
                        break;
                    case 0xD7:
                        skipBytes = 9;
                        break;
                    case 0xD8:
                        skipBytes = 17;
                        break;
                    case 0xC4:
                    case 0xD9:
                        skipBytes = u8();
                        break;
                    case 0xC5:
                    case 0xDA:
                        skipBytes = u16();
                        break;
                    case 0xC6:
                    case 0xDB:
                        skipBytes = i32() & 0xFFFF_FFFFL;
                        break;
                    case 0xC7:
                        skipBytes = u8() + 1L;
                        break;
                    case 0xC8:
                        skipBytes = u16() + 1L;
                        break;
                    case 0xC9:
                        skipBytes = (i32() & 0xFFFF_FFFFL) + 1;
                        break;
                    case 0xDC:
                        pending += u16();
                        continue;
                    case 0xDD:
                        pending += i32() & 0xFFFF_FFFFL;
                        continue;
                    case 0xDE:
                        pending += 2L * u16();
                        continue;
                    case 0xDF:
                        pending += 2L * (i32() & 0xFFFF_FFFFL);
                        continue;
                    default:
                        throw unexpected(c, "a value"); // 0xc1
                }
            }
            need(skipBytes);
            position += (int) skipBytes;
            if (pending > end - position) {
                // Every pending value needs at least one byte.
                throw truncated();
            }
        }
    }
}
