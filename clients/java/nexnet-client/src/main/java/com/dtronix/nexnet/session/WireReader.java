package com.dtronix.nexnet.session;

import java.nio.charset.StandardCharsets;
import org.jspecify.annotations.Nullable;

/**
 * Reads the MessagePack forms of protocol message bodies (see {@link WireWriter}). Accepts every integer encoding,
 * not only the canonical one. Malformed or truncated input throws {@link WireFormatException}.
 */
final class WireReader {
    private final byte[] data;
    private int position;
    private final int end;

    WireReader(byte[] data, int offset, int length) {
        this.data = data;
        this.position = offset;
        this.end = offset + length;
    }

    int remaining() {
        return end - position;
    }

    int readArrayHeader() {
        int b = u8();
        if ((b & 0xF0) == 0x90) {
            return b & 0x0F;
        }
        if (b == 0xDC) {
            return u16();
        }
        if (b == 0xDD) {
            long count = u32();
            if (count > Integer.MAX_VALUE) {
                throw new WireFormatException("Array too long.");
            }
            return (int) count;
        }
        throw new WireFormatException("Expected an array, found 0x" + Integer.toHexString(b));
    }

    boolean tryReadNil() {
        need(1);
        if ((data[position] & 0xFF) == 0xC0) {
            position++;
            return true;
        }
        return false;
    }

    long readLong() {
        int b = u8();
        if (b <= 0x7F) {
            return b;
        }
        if (b >= 0xE0) {
            return (byte) b;
        }
        switch (b) {
            case 0xCC:
                return u8();
            case 0xCD:
                return u16();
            case 0xCE:
                return u32();
            case 0xCF: {
                long v = s64();
                if (v < 0) {
                    throw new WireFormatException("Integer out of range.");
                }
                return v;
            }
            case 0xD0:
                return (byte) u8();
            case 0xD1:
                return (short) u16();
            case 0xD2:
                return (int) u32();
            case 0xD3:
                return s64();
            default:
                throw new WireFormatException("Expected an integer, found 0x" + Integer.toHexString(b));
        }
    }

    /** Reads a MessagePack integer in the uint32 range, as C# reads a {@code uint}. */
    long readUInt32() {
        long v = readLong();
        if (v < 0 || v > 0xFFFF_FFFFL) {
            throw new WireFormatException("Integer out of uint32 range: " + v);
        }
        return v;
    }

    int readInt() {
        long v = readLong();
        if (v < Integer.MIN_VALUE || v > Integer.MAX_VALUE) {
            throw new WireFormatException("Integer out of int32 range: " + v);
        }
        return (int) v;
    }

    @Nullable String readString() {
        if (tryReadNil()) {
            return null;
        }
        int b = u8();
        int length;
        if ((b & 0xE0) == 0xA0) {
            length = b & 0x1F;
        } else if (b == 0xD9) {
            length = u8();
        } else if (b == 0xDA) {
            length = u16();
        } else if (b == 0xDB) {
            length = checkedLength(u32());
        } else {
            throw new WireFormatException("Expected a string, found 0x" + Integer.toHexString(b));
        }
        need(length);
        String s = new String(data, position, length, StandardCharsets.UTF_8);
        position += length;
        return s;
    }

    /**
     * Reads bin or nil (an empty array), refusing a declared length above the limit before the bytes are read.
     */
    byte[] readBinary(int maxLength) {
        if (tryReadNil()) {
            return new byte[0];
        }
        int b = u8();
        int length;
        if (b == 0xC4) {
            length = u8();
        } else if (b == 0xC5) {
            length = u16();
        } else if (b == 0xC6) {
            length = checkedLength(u32());
        } else {
            throw new WireFormatException("Expected bin, found 0x" + Integer.toHexString(b));
        }
        if (length > maxLength) {
            throw new WireFormatException("bin longer than " + maxLength + " bytes.");
        }
        need(length);
        byte[] bytes = new byte[length];
        System.arraycopy(data, position, bytes, 0, length);
        position += length;
        return bytes;
    }

    /** Skips one value of any type. */
    void skip() {
        int b = u8();
        if (b <= 0x7F || b >= 0xE0 || b == 0xC0 || b == 0xC2 || b == 0xC3) {
            return;
        }
        if ((b & 0xF0) == 0x80) {
            skipValues(2L * (b & 0x0F));
            return;
        }
        if ((b & 0xF0) == 0x90) {
            skipValues(b & 0x0F);
            return;
        }
        if ((b & 0xE0) == 0xA0) {
            advance(b & 0x1F);
            return;
        }
        switch (b) {
            case 0xC4: case 0xD9: advance(u8()); return;
            case 0xC5: case 0xDA: advance(u16()); return;
            case 0xC6: case 0xDB: advance(checkedLength(u32())); return;
            case 0xC7: advance(u8() + 1); return;
            case 0xC8: advance(u16() + 1); return;
            case 0xC9: advance(checkedLength(u32()) + 1); return;
            case 0xCA: advance(4); return;
            case 0xCB: advance(8); return;
            case 0xCC: case 0xD0: advance(1); return;
            case 0xCD: case 0xD1: advance(2); return;
            case 0xCE: case 0xD2: advance(4); return;
            case 0xCF: case 0xD3: advance(8); return;
            case 0xD4: advance(2); return;
            case 0xD5: advance(3); return;
            case 0xD6: advance(5); return;
            case 0xD7: advance(9); return;
            case 0xD8: advance(17); return;
            case 0xDC: skipValues(u16()); return;
            case 0xDD: skipValues(u32()); return;
            case 0xDE: skipValues(2L * u16()); return;
            case 0xDF: skipValues(2L * u32()); return;
            default: throw new WireFormatException("Invalid MessagePack type 0x" + Integer.toHexString(b));
        }
    }

    private void skipValues(long count) {
        if (count > remaining()) {
            throw new WireFormatException("Truncated body.");
        }
        for (long i = 0; i < count; i++) {
            skip();
        }
    }

    private static int checkedLength(long length) {
        if (length > Integer.MAX_VALUE - 1) {
            throw new WireFormatException("Length out of range.");
        }
        return (int) length;
    }

    private void advance(int count) {
        need(count);
        position += count;
    }

    private void need(int count) {
        if (count < 0 || end - position < count) {
            throw new WireFormatException("Truncated body.");
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

    private long u32() {
        need(4);
        long v = ((long) (data[position] & 0xFF) << 24) | ((data[position + 1] & 0xFF) << 16)
            | ((data[position + 2] & 0xFF) << 8) | (data[position + 3] & 0xFF);
        position += 4;
        return v;
    }

    private long s64() {
        long high = u32();
        long low = u32();
        return (high << 32) | low;
    }
}
