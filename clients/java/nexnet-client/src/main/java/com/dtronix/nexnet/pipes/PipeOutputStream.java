package com.dtronix.nexnet.pipes;

import java.io.IOException;
import java.io.InterruptedIOException;
import java.io.OutputStream;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;

/**
 * A blocking {@link OutputStream} over {@link NexusDuplexPipe#write}: each write blocks until its bytes are within the
 * peer's credit. {@link #close()} completes the pipe's output. Never use it on the Android main thread.
 */
final class PipeOutputStream extends OutputStream {
    private final PipeImpl pipe;

    PipeOutputStream(PipeImpl pipe) {
        this.pipe = pipe;
    }

    @Override
    public void write(int b) throws IOException {
        write(new byte[] {(byte) b}, 0, 1);
    }

    @Override
    public void write(byte[] b, int off, int len) throws IOException {
        if (off < 0 || len < 0 || len > b.length - off) {
            throw new IndexOutOfBoundsException();
        }
        byte[] copy = new byte[len];
        System.arraycopy(b, off, copy, 0, len);
        await(pipe.writeBytes(copy));
    }

    /** Completes the pipe's output and waits until that is sent. */
    @Override
    public void close() throws IOException {
        await(pipe.completeOutput());
    }

    private static void await(CompletableFuture<Void> future) throws IOException {
        try {
            future.get();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new InterruptedIOException("Interrupted while writing the pipe.");
        } catch (ExecutionException e) {
            throw new IOException("The pipe write failed.", e.getCause());
        }
    }
}
