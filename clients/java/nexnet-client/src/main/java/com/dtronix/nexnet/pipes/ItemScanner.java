package com.dtronix.nexnet.pipes;

/**
 * Finds where one MessagePack value ends, without decoding it, so a channel item can be checked to be exactly one
 * value of its prefixed length (protocol specification section 7.4) whatever the item formatter reads.
 */
final class ItemScanner {
    private ItemScanner() {
    }

    /**
     * The end of the value starting at {@code offset}.
     *
     * @param b the bytes
     * @param offset the first byte of the value
     * @param end the end of the readable bytes
     * @return the position after the value, or -1 if it is malformed or does not end before {@code end}
     */
    static int valueEnd(byte[] b, int offset, int end) {
        long pending = 1;
        long p = offset;
        while (pending > 0) {
            if (p >= end) {
                return -1;
            }
            int t = b[(int) p++] & 0xFF;
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
                p += t & 0x1F;
                continue;
            }
            long skip;
            switch (t) {
                case 0xC0:
                case 0xC2:
                case 0xC3:
                    continue;
                case 0xC4:
                case 0xD9:
                    skip = read(b, p, 1, end);
                    p += 1;
                    break;
                case 0xC5:
                case 0xDA:
                    skip = read(b, p, 2, end);
                    p += 2;
                    break;
                case 0xC6:
                case 0xDB:
                    skip = read(b, p, 4, end);
                    p += 4;
                    break;
                case 0xC7:
                    skip = read(b, p, 1, end) + 1;
                    p += 1;
                    break;
                case 0xC8:
                    skip = read(b, p, 2, end) + 1;
                    p += 2;
                    break;
                case 0xC9:
                    skip = read(b, p, 4, end) + 1;
                    p += 4;
                    break;
                case 0xCC:
                case 0xD0:
                    skip = 1;
                    break;
                case 0xCD:
                case 0xD1:
                    skip = 2;
                    break;
                case 0xCA:
                case 0xCE:
                case 0xD2:
                    skip = 4;
                    break;
                case 0xCB:
                case 0xCF:
                case 0xD3:
                    skip = 8;
                    break;
                case 0xD4:
                    skip = 2;
                    break;
                case 0xD5:
                    skip = 3;
                    break;
                case 0xD6:
                    skip = 5;
                    break;
                case 0xD7:
                    skip = 9;
                    break;
                case 0xD8:
                    skip = 17;
                    break;
                case 0xDC:
                    pending += read(b, p, 2, end);
                    p += 2;
                    continue;
                case 0xDD:
                    pending += read(b, p, 4, end);
                    p += 4;
                    continue;
                case 0xDE:
                    pending += 2 * read(b, p, 2, end);
                    p += 2;
                    continue;
                case 0xDF:
                    pending += 2 * read(b, p, 4, end);
                    p += 4;
                    continue;
                default:
                    // 0xC1 is never used.
                    return -1;
            }
            if (skip < 0) {
                return -1;
            }
            p += skip;
        }
        return p > end ? -1 : (int) p;
    }

    /** A big-endian length of {@code bytes} bytes at {@code p}, or a huge value that runs past the end. */
    private static long read(byte[] b, long p, int bytes, int end) {
        if (p + bytes > end) {
            return Integer.MAX_VALUE;
        }
        long value = 0;
        for (int i = 0; i < bytes; i++) {
            value = (value << 8) | (b[(int) p + i] & 0xFF);
        }
        return value;
    }
}
