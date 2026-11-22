package com.dtronix.nexnet.session;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;

/** Buffered blocking reads for the reader thread. A frame may span any number of reads. */
final class FrameInput {
    private final InputStream in;
    private final byte[] buffer = new byte[16 * 1024];
    private int position;
    private int limit;

    FrameInput(InputStream in) {
        this.in = in;
    }

    /** The next byte, or -1 at end of stream. */
    int read() throws IOException {
        if (position == limit && !fill()) {
            return -1;
        }
        return buffer[position++] & 0xFF;
    }

    void readFully(byte[] destination, int offset, int length) throws IOException {
        while (length > 0) {
            if (position == limit && !fill()) {
                throw new EOFException("The stream ended inside a frame.");
            }
            int n = Math.min(length, limit - position);
            System.arraycopy(buffer, position, destination, offset, n);
            position += n;
            offset += n;
            length -= n;
        }
    }

    /** Reads a little-endian unsigned value of 0 to 4 bytes. */
    int readLittleEndian(int bytes) throws IOException {
        int value = 0;
        for (int i = 0; i < bytes; i++) {
            int b = read();
            if (b < 0) {
                throw new EOFException("The stream ended inside a frame header.");
            }
            value |= b << (8 * i);
        }
        return value;
    }

    private boolean fill() throws IOException {
        int n;
        do {
            n = in.read(buffer, 0, buffer.length);
        } while (n == 0);
        if (n < 0) {
            return false;
        }
        position = 0;
        limit = n;
        return true;
    }
}
