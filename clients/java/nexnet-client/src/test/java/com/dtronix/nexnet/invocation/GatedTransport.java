package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.io.OutputStream;

/** A loopback transport whose client-to-server direction writes at most an allowance of bytes, then blocks. */
final class GatedTransport implements Transport {
    private final LoopbackTransport inner = new LoopbackTransport();
    private final Object gate = new Object();
    private long allowance = Long.MAX_VALUE;
    private boolean closed;

    private final OutputStream output = new OutputStream() {
        @Override
        public void write(int b) throws IOException {
            write(new byte[] {(byte) b}, 0, 1);
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            while (len > 0) {
                int n;
                synchronized (gate) {
                    while (allowance <= 0 && !closed) {
                        try {
                            gate.wait();
                        } catch (InterruptedException e) {
                            Thread.currentThread().interrupt();
                            throw new InterruptedIOException();
                        }
                    }
                    if (closed) {
                        throw new IOException("Closed.");
                    }
                    n = (int) Math.min(len, allowance);
                    allowance -= n;
                }
                inner.output().write(b, off, n);
                off += n;
                len -= n;
            }
        }

        @Override
        public void flush() throws IOException {
            inner.output().flush();
        }
    };

    LoopbackTransport inner() {
        return inner;
    }

    /** Lets {@code bytes} more bytes through, then blocks again. */
    void allow(long bytes) {
        synchronized (gate) {
            allowance = Math.max(0, allowance) + bytes;
            gate.notifyAll();
        }
    }

    /** Blocks writes after {@code bytes} more bytes. */
    void limit(long bytes) {
        synchronized (gate) {
            allowance = bytes;
            gate.notifyAll();
        }
    }

    /** Lets everything through. */
    void open() {
        synchronized (gate) {
            allowance = Long.MAX_VALUE;
            gate.notifyAll();
        }
    }

    @Override
    public InputStream input() {
        return inner.input();
    }

    @Override
    public OutputStream output() {
        return output;
    }

    @Override
    public String name() {
        return "gated";
    }

    @Override
    public void close(DisconnectReason reason) {
        synchronized (gate) {
            closed = true;
            gate.notifyAll();
        }
        inner.close(reason);
    }
}
