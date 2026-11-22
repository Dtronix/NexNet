package com.dtronix.nexnet.serialization;

/**
 * UTF-8 transcoding with the same results as .NET on every platform: an unpaired surrogate is written as U+FFFD, and
 * lenient decoding replaces each maximal invalid subpart (Unicode 15, section 3.9) with one U+FFFD. Hand-written
 * rather than a {@code CharsetDecoder} with {@code REPORT}/{@code REPLACE}, whose replacement granularity differs
 * between JDK and Android (ICU) implementations.
 */
final class Utf8 {
    private static final char REPLACEMENT = (char) 0xFFFD;

    private Utf8() {
    }

    /** The number of UTF-8 bytes of a string. */
    static int length(String value) {
        int n = value.length();
        int bytes = n;
        for (int i = 0; i < n; i++) {
            char c = value.charAt(i);
            if (c < 0x80) {
                continue;
            }
            if (c < 0x800) {
                bytes += 1;
            } else if (Character.isHighSurrogate(c) && i + 1 < n && Character.isLowSurrogate(value.charAt(i + 1))) {
                bytes += 2; // four bytes for two chars
                i++;
            } else {
                bytes += 2; // three bytes, also for an unpaired surrogate (U+FFFD)
            }
        }
        return bytes;
    }

    /** Encodes a string at {@code offset}; returns the offset after it. */
    static int encode(String value, byte[] dest, int offset) {
        int n = value.length();
        int p = offset;
        for (int i = 0; i < n; i++) {
            char c = value.charAt(i);
            if (c < 0x80) {
                dest[p++] = (byte) c;
            } else if (c < 0x800) {
                dest[p++] = (byte) (0xC0 | (c >> 6));
                dest[p++] = (byte) (0x80 | (c & 0x3F));
            } else if (Character.isSurrogate(c)) {
                if (Character.isHighSurrogate(c) && i + 1 < n && Character.isLowSurrogate(value.charAt(i + 1))) {
                    int cp = Character.toCodePoint(c, value.charAt(++i));
                    dest[p++] = (byte) (0xF0 | (cp >> 18));
                    dest[p++] = (byte) (0x80 | ((cp >> 12) & 0x3F));
                    dest[p++] = (byte) (0x80 | ((cp >> 6) & 0x3F));
                    dest[p++] = (byte) (0x80 | (cp & 0x3F));
                } else {
                    dest[p++] = (byte) 0xEF;
                    dest[p++] = (byte) 0xBF;
                    dest[p++] = (byte) 0xBD;
                }
            } else {
                dest[p++] = (byte) (0xE0 | (c >> 12));
                dest[p++] = (byte) (0x80 | ((c >> 6) & 0x3F));
                dest[p++] = (byte) (0x80 | (c & 0x3F));
            }
        }
        return p;
    }

    /**
     * Decodes {@code length} bytes.
     *
     * @throws MsgPackException ({@link MsgPackException.Kind#UTF8}) on invalid UTF-8 when {@code strict}
     */
    static String decode(byte[] data, int offset, int length, boolean strict) {
        int end = offset + length;
        int i = offset;
        while (i < end && data[i] >= 0) {
            i++;
        }
        char[] out = new char[length]; // never more UTF-16 units than UTF-8 bytes
        int o = 0;
        for (int k = offset; k < i; k++) {
            out[o++] = (char) data[k];
        }
        while (i < end) {
            int b = data[i] & 0xFF;
            if (b < 0x80) {
                out[o++] = (char) b;
                i++;
                continue;
            }
            int need;
            int cp;
            int lower = 0x80;
            int upper = 0xBF;
            if (b >= 0xC2 && b <= 0xDF) {
                need = 1;
                cp = b & 0x1F;
            } else if (b >= 0xE0 && b <= 0xEF) {
                need = 2;
                cp = b & 0x0F;
                if (b == 0xE0) {
                    lower = 0xA0;
                } else if (b == 0xED) {
                    upper = 0x9F;
                }
            } else if (b >= 0xF0 && b <= 0xF4) {
                need = 3;
                cp = b & 0x07;
                if (b == 0xF0) {
                    lower = 0x90;
                } else if (b == 0xF4) {
                    upper = 0x8F;
                }
            } else {
                o = invalid(out, o, strict);
                i++;
                continue;
            }
            int j = i + 1;
            boolean ok = true;
            for (int k = 0; k < need; k++) {
                if (j >= end) {
                    ok = false;
                    break;
                }
                int c = data[j] & 0xFF;
                if (c < lower || c > upper) {
                    ok = false;
                    break;
                }
                lower = 0x80;
                upper = 0xBF;
                cp = (cp << 6) | (c & 0x3F);
                j++;
            }
            if (!ok) {
                // The maximal subpart is bytes i..j-1; the byte at j starts the next sequence.
                o = invalid(out, o, strict);
                i = j;
                continue;
            }
            if (cp >= 0x10000) {
                out[o++] = Character.highSurrogate(cp);
                out[o++] = Character.lowSurrogate(cp);
            } else {
                out[o++] = (char) cp;
            }
            i = j;
        }
        return new String(out, 0, o);
    }

    private static int invalid(char[] out, int o, boolean strict) {
        if (strict) {
            throw new MsgPackException(MsgPackException.Kind.UTF8, "Invalid UTF-8 in a string.");
        }
        out[o] = REPLACEMENT;
        return o + 1;
    }
}
