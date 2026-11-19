package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.serialization.Formatter;
import java.util.Collections;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * Writes channel items to a pipe (protocol specification section 7.4). Items are serialized and checked against the
 * server's {@code maxMessageSize} before anything is queued: an item too large (or one the formatter can't write)
 * fails the write and nothing of it, or of the other items of the same call, is sent; the channel stays usable.
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
        byte[] framed;
        try {
            framed = frame(items);
        } catch (RuntimeException e) {
            CompletableFuture<Void> failed = new CompletableFuture<>();
            failed.completeExceptionally(e);
            return failed;
        }
        // One write: the items go out with one flush.
        return pipe.writeBytes(framed);
    }

    @Override
    public CompletableFuture<Void> complete() {
        return pipe.completeOutput();
    }

    @Override
    public CompletableFuture<Void> writeAndComplete(Iterable<? extends T> items) {
        byte[] framed;
        try {
            framed = frame(items);
        } catch (RuntimeException e) {
            CompletableFuture<Void> failed = new CompletableFuture<>();
            failed.completeExceptionally(e);
            return failed;
        }
        CompletableFuture<Void> written = pipe.writeBytes(framed);
        CompletableFuture<Void> completed = pipe.completeOutput();
        return written.thenCompose(v -> completed);
    }

    /** Serializes the items, each preceded by its length. */
    private byte[] frame(Iterable<? extends T> items) {
        if (items == null) {
            throw new NullPointerException("items");
        }
        int limit = manager.sendMaxItemSize();
        byte[] out = new byte[64];
        int size = 0;
        for (T item : items) {
            byte[] value = manager.codec().writeValue(formatter, item);
            if (value.length > limit) {
                throw new NexusMessageTooLargeException(value.length, limit);
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
        if (size == out.length) {
            return out;
        }
        byte[] exact = new byte[size];
        System.arraycopy(out, 0, exact, 0, size);
        return exact;
    }
}
