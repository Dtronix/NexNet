package com.dtronix.nexnet.session;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.SocketTimeoutException;
import java.util.ArrayDeque;

/** An in-memory one-way byte stream: blocking reads with an optional timeout, end of stream once closed. */
public final class BytePipe {
    private final ArrayDeque<Byte> bytes = new ArrayDeque<>();
    private boolean closed;
    private volatile long readTimeoutMillis;

    private final InputStream input = new InputStream() {
        @Override
        public int read() throws IOException {
            byte[] one = new byte[1];
            int n = read(one, 0, 1);
            return n < 0 ? -1 : one[0] & 0xFF;
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            if (len == 0) {
                return 0;
            }
            synchronized (bytes) {
                long timeout = readTimeoutMillis;
                long deadline = System.currentTimeMillis() + timeout;
                while (bytes.isEmpty() && !closed) {
                    try {
                        if (timeout > 0) {
                            long left = deadline - System.currentTimeMillis();
                            if (left <= 0) {
                                throw new SocketTimeoutException("No data.");
                            }
                            bytes.wait(left);
                        } else {
                            bytes.wait();
                        }
                    } catch (InterruptedException e) {
                        Thread.currentThread().interrupt();
                        throw new IOException(e);
                    }
                }
                if (bytes.isEmpty()) {
                    return -1;
                }
                int n = 0;
                while (n < len && !bytes.isEmpty()) {
                    b[off + n++] = bytes.poll();
                }
                return n;
            }
        }
    };

    private final OutputStream output = new OutputStream() {
        @Override
        public void write(int b) throws IOException {
            write(new byte[] {(byte) b}, 0, 1);
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            synchronized (bytes) {
                if (closed) {
                    throw new IOException("The pipe is closed.");
                }
                for (int i = 0; i < len; i++) {
                    bytes.add(b[off + i]);
                }
                bytes.notifyAll();
            }
        }
    };

    /** The reading end. */
    public InputStream input() {
        return input;
    }

    /** The writing end. */
    public OutputStream output() {
        return output;
    }

    /** Sets the read timeout; 0 waits forever. */
    public void readTimeoutMillis(long millis) {
        readTimeoutMillis = millis;
    }

    /** Closes: readers see end of stream once drained, writers fail. */
    public void close() {
        synchronized (bytes) {
            closed = true;
            bytes.notifyAll();
        }
    }
}
