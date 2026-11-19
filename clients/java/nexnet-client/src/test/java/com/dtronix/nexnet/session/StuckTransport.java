package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.io.OutputStream;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * A loopback transport whose outbound writes, once {@link #stick()} is called, block until the transport closes: a
 * peer that stopped reading, or a network path that stopped moving data.
 */
final class StuckTransport implements Transport {
    private final LoopbackTransport inner;
    private final Object gate = new Object();
    private final CountDownLatch blocked = new CountDownLatch(1);
    private boolean stuck;
    private boolean closed;

    private final OutputStream output = new OutputStream() {
        @Override
        public void write(int b) throws IOException {
            write(new byte[] {(byte) b}, 0, 1);
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            synchronized (gate) {
                while (stuck && !closed) {
                    blocked.countDown();
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
            }
            inner.output().write(b, off, len);
        }

        @Override
        public void flush() throws IOException {
            inner.output().flush();
        }
    };

    StuckTransport(LoopbackTransport inner) {
        this.inner = inner;
    }

    /** Makes every later write block until the transport closes. */
    void stick() {
        synchronized (gate) {
            stuck = true;
        }
    }

    /** Waits until a write is blocked. */
    boolean awaitBlocked() throws InterruptedException {
        return blocked.await(5, TimeUnit.SECONDS);
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
        return "stuck";
    }

    @Override
    public String remoteAddress() {
        return inner.remoteAddress();
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
