package com.dtronix.nexnet.pipes;

import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.nio.ByteBuffer;
import java.util.concurrent.ExecutionException;
import org.jspecify.annotations.Nullable;

/** A blocking {@link InputStream} over {@link NexusDuplexPipe#read()}. Never use it on the Android main thread. */
final class PipeInputStream extends InputStream {
    private final NexusDuplexPipe pipe;
    private @Nullable ByteBuffer current;
    private boolean ended;

    PipeInputStream(NexusDuplexPipe pipe) {
        this.pipe = pipe;
    }

    @Override
    public int read() throws IOException {
        byte[] one = new byte[1];
        int n = read(one, 0, 1);
        return n < 0 ? -1 : one[0] & 0xFF;
    }

    @Override
    public synchronized int read(byte[] b, int off, int len) throws IOException {
        if (off < 0 || len < 0 || len > b.length - off) {
            throw new IndexOutOfBoundsException();
        }
        if (len == 0) {
            return 0;
        }
        ByteBuffer buffer = next();
        if (buffer == null) {
            return -1;
        }
        int n = Math.min(len, buffer.remaining());
        buffer.get(b, off, n);
        return n;
    }

    @Override
    public synchronized int available() {
        ByteBuffer buffer = current;
        return buffer == null ? 0 : buffer.remaining();
    }

    private @Nullable ByteBuffer next() throws IOException {
        while (true) {
            ByteBuffer buffer = current;
            if (buffer != null && buffer.hasRemaining()) {
                return buffer;
            }
            if (ended) {
                return null;
            }
            try {
                current = pipe.read().get();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new InterruptedIOException("Interrupted while reading the pipe.");
            } catch (ExecutionException e) {
                throw new IOException("The pipe read failed.", e.getCause());
            }
            if (current == null) {
                ended = true;
            }
        }
    }

    /** Closes the pipe. */
    @Override
    public void close() {
        pipe.close();
    }
}
