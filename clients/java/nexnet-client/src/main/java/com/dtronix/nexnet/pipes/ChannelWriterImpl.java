package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.serialization.Formatter;
import java.util.Collections;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * Writes channel items to a pipe (protocol specification section 7.4). Each item is serialized and checked against
 * the server's {@code maxMessageSize}. An item too large (or one the formatter can't write) fails the write and is
 * not sent, nor are the items after it in the same call; the items before it are sent, as in C#
 * ({@code NexusChannelWriter.WriteAsync(IEnumerable)}). The channel stays usable.
 */
final class ChannelWriterImpl<T> implements NexusChannelWriter<T> {
    private final PipeImpl pipe;
    private final Formatter<T> formatter;
    private final PipeManager manager;

    ChannelWriterImpl(PipeImpl pipe, Formatter<T> formatter, PipeManager manager) {
        this.pipe = pipe;
        this.formatter = formatter;
        this.manager = manager;
    }

    @Override
    public CompletableFuture<Void> write(@Nullable T item) {
        return writeAll(Collections.singletonList(item));
    }

    @Override
    public CompletableFuture<Void> writeAll(Iterable<? extends T> items) {
        if (items == null) {
            return failAfter(CompletableFuture.completedFuture(null), new NullPointerException("items"));
        }
        Framed framed = frame(items);
        // One write: the items go out with one flush.
        CompletableFuture<Void> written = framed.length == 0 && framed.error != null
            ? CompletableFuture.completedFuture(null) : pipe.writeBytes(framed.bytes());
        return framed.error == null ? written : failAfter(written, framed.error);
    }

    @Override
    public CompletableFuture<Void> complete() {
        return pipe.completeOutput();
    }

    @Override
    public CompletableFuture<Void> writeAndComplete(Iterable<? extends T> items) {
        if (items == null) {
            return failAfter(CompletableFuture.completedFuture(null), new NullPointerException("items"));
        }
        Framed framed = frame(items);
        if (framed.error != null) {
            // The items before the bad one are sent; the channel is not completed.
            CompletableFuture<Void> written = framed.length == 0
                ? CompletableFuture.completedFuture(null) : pipe.writeBytes(framed.bytes());
            return failAfter(written, framed.error);
        }
        CompletableFuture<Void> written = pipe.writeBytes(framed.bytes());
        CompletableFuture<Void> completed = pipe.completeOutput();
        return written.thenCompose(v -> completed);
    }

    /** Fails with {@code error} once {@code written} settles, so the items before the bad one are queued first. */
    private static CompletableFuture<Void> failAfter(CompletableFuture<Void> written, RuntimeException error) {
        CompletableFuture<Void> failed = new CompletableFuture<>();
        written.whenComplete((v, e) -> {
            if (e != null) {
                error.addSuppressed(PipeManager.unwrap(e));
            }
            failed.completeExceptionally(error);
        });
        return failed;
    }

    /** Items serialized with their length prefixes, up to the first that failed. */
    private static final class Framed {
        final byte[] buffer;
        final int length;
        final @Nullable RuntimeException error;

        Framed(byte[] buffer, int length, @Nullable RuntimeException error) {
            this.buffer = buffer;
            this.length = length;
            this.error = error;
        }

        byte[] bytes() {
            if (length == buffer.length) {
                return buffer;
            }
            byte[] exact = new byte[length];
            System.arraycopy(buffer, 0, exact, 0, length);
            return exact;
        }
    }

    /**
     * Serializes the items, each preceded by its length, stopping at the first item that is too large or that the
     * formatter can't write.
     */
    private Framed frame(Iterable<? extends T> items) {
        int limit = manager.sendMaxItemSize();
        byte[] out = new byte[64];
        int size = 0;
        try {
            for (T item : items) {
                byte[] value = manager.codec().writeValue(formatter, item);
                if (value.length > limit) {
                    return new Framed(out, size, new NexusMessageTooLargeException(value.length, limit));
                }
                int needed = size + PipeWire.prefixLength(value.length) + value.length;
                if (needed > out.length) {
                    byte[] grown = new byte[Math.max(needed, out.length * 2)];
                    System.arraycopy(out, 0, grown, 0, size);
                    out = grown;
                }
                size = PipeWire.writePrefix(out, size, value.length);
                System.arraycopy(value, 0, out, size, value.length);
                size += value.length;
            }
        } catch (RuntimeException e) {
            // The formatter (or the iterable) failed: the items before are still sent.
            return new Framed(out, size, e);
        }
        return new Framed(out, size, null);
    }
}
