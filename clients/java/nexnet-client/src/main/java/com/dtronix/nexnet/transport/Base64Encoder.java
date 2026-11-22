package com.dtronix.nexnet.transport;

/**
 * A minimal standard Base64 encoder (RFC 4648, with padding). {@code java.util.Base64} is API 26 and
 * {@code android.util.Base64} is not on the JVM, so the WebSocket key uses this.
 */
final class Base64Encoder {
    private static final char[] ALPHABET =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".toCharArray();

    private Base64Encoder() {
    }

    static String encode(byte[] data) {
        StringBuilder sb = new StringBuilder((data.length + 2) / 3 * 4);
        int i = 0;
        for (; i + 2 < data.length; i += 3) {
            int n = (data[i] & 0xFF) << 16 | (data[i + 1] & 0xFF) << 8 | (data[i + 2] & 0xFF);
            sb.append(ALPHABET[n >>> 18]).append(ALPHABET[(n >>> 12) & 63])
                .append(ALPHABET[(n >>> 6) & 63]).append(ALPHABET[n & 63]);
        }
        int left = data.length - i;
        if (left == 1) {
            int n = (data[i] & 0xFF) << 16;
            sb.append(ALPHABET[n >>> 18]).append(ALPHABET[(n >>> 12) & 63]).append("==");
        } else if (left == 2) {
            int n = (data[i] & 0xFF) << 16 | (data[i + 1] & 0xFF) << 8;
            sb.append(ALPHABET[n >>> 18]).append(ALPHABET[(n >>> 12) & 63]).append(ALPHABET[(n >>> 6) & 63])
                .append('=');
        }
        return sb.toString();
    }
}
