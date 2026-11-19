package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;
import java.util.function.Supplier;

/**
 * Reads channel items from a pipe (protocol specification section 7.4): each item is an unsigned LEB128 length
 * followed by exactly one MessagePack value of that length. Items are consumed from the pipe (and so credited back to
 * the writer) only once they are decoded. An item that breaks the framing resets the pipe; the session stays up.
 * Reads are served one at a time, in call order.
 */
final class ChannelReaderImpl<T> implements NexusChannelReader<T> {
    private final PipeImpl pipe;
    private final Formatter<T> formatter;
    private final PipeManager manager;
    private final Object lock = new Object();
    private CompletableFuture<?> tail = CompletableFuture.completedFuture(null);
    /** Bytes at the start of the pipe's buffer copied here and not yet consumed. Used by one read at a time. */
    private byte[] examined = new byte[0];
    private boolean failed;

    ChannelReaderImpl(PipeImpl pipe, Formatter<T> formatter, PipeManager manager) {
        this.pipe = pipe;
        this.formatter = formatter;
        this.manager = manager;
    }

    @Override
    public CompletableFuture<Optional<T>> read() {
        return readItem().thenApply(result -> {
            if (result.isCompleted()) {
                return Optional.<T>empty();
            }
            T value = result.value();
            if (value == null) {
                // An empty optional would read as the end of the channel.
                throw new NullPointerException("The channel item is null; use readItem() for a nullable item type.");
            }
            return Optional.of(value);
        });
    }

    @Override
    public CompletableFuture<ChannelReadResult<T>> readItem() {
        return serialized(() -> fill(1)).thenApply(items -> items.isEmpty()
            ? ChannelReadResult.<T>completed() : ChannelReadResult.of(items.get(0)));
    }

    @Override
    public CompletableFuture<List<T>> readBatch(int max) {
        if (max < 1) {
            throw new IllegalArgumentException("max must be at least 1");
        }
        return serialized(() -> fill(max));
    }

    @Override
    public CompletableFuture<List<T>> readUntilComplete() {
        return serialized(() -> collect(new ArrayList<>()));
    }

    private CompletableFuture<List<T>> collect(List<T> all) {
        return fill(Integer.MAX_VALUE).thenCompose(items -> {
            if (items.isEmpty()) {
                return CompletableFuture.completedFuture(all);
            }
            all.addAll(items);
            return collect(all);
        });
    }

    private <R> CompletableFuture<R> serialized(Supplier<CompletableFuture<R>> operation) {
        synchronized (lock) {
            CompletableFuture<R> next = tail.handle((v, e) -> null).thenCompose(v -> operation.get());
            tail = next;
            return next;
        }
    }

    /** Decodes up to {@code max} items, waiting for at least one; empty at the end of the channel. */
    private CompletableFuture<List<T>> fill(int max) {
        if (failed) {
            return CompletableFuture.completedFuture(Collections.emptyList());
        }
        // Checked before looking at the data: data is never added after the input ends.
        boolean ended = pipe.isInputComplete();
        byte[] more = pipe.peek(examined.length);
        if (more.length > 0) {
            byte[] joined = Arrays.copyOf(examined, examined.length + more.length);
            System.arraycopy(more, 0, joined, examined.length, more.length);
            examined = joined;
        }
        List<T> items = new ArrayList<>();
        int position = 0;
        long pendingItemLength = 0;
        long missing = 0;
        try {
            while (items.size() < max && position < examined.length) {
                long length = 0;
                int p = position;
                boolean prefixDone = false;
                for (int i = 0; i < PipeWire.MAX_PREFIX_LENGTH; i++) {
                    if (p >= examined.length) {
                        break;
                    }
                    int b = examined[p++] & 0xFF;
                    length |= (long) (b & 0x7F) << (7 * i);
                    if ((b & 0x80) == 0) {
                        prefixDone = true;
                        break;
                    }
                    if (i == PipeWire.MAX_PREFIX_LENGTH - 1) {
                        throw new MsgPackException("Channel item length prefix is longer than five bytes.");
                    }
                }
                if (!prefixDone) {
                    break;
                }
                if (length > Integer.MAX_VALUE) {
                    throw new MsgPackException("Channel item length prefix exceeds the maximum length.");
                }
                if (length > manager.maxItemSize()) {
                    throw new NexusMessageTooLargeException(length, manager.maxItemSize());
                }
                int itemStart = p;
                if (examined.length - itemStart < length) {
                    pendingItemLength = itemStart - position + length;
                    missing = length - (examined.length - itemStart);
                    break;
                }
                int itemEnd = itemStart + (int) length;
                if (ItemScanner.valueEnd(examined, itemStart, itemEnd) != itemEnd) {
                    throw new MsgPackException("Channel item does not decode to exactly its length.");
                }
                T item;
                try {
                    item = manager.codec().readValue(formatter, examined, itemStart, (int) length);
                } catch (MsgPackException e) {
                    throw e;
                } catch (RuntimeException e) {
                    throw new MsgPackException("Channel item does not decode: " + e.getMessage(), e);
                }
                items.add(item);
                position = itemEnd;
            }
        } catch (MsgPackException | NexusMessageTooLargeException e) {
            failed = true;
            examined = new byte[0];
            pipe.reset();
            CompletableFuture<List<T>> error = new CompletableFuture<>();
            error.completeExceptionally(e);
            return error;
        }
        if (position > 0) {
            pipe.consume(position);
            examined = Arrays.copyOfRange(examined, position, examined.length);
        }
        if (!items.isEmpty()) {
            return CompletableFuture.completedFuture(items);
        }
        if (ended) {
            // An incomplete item at the end is dropped, as in C#.
            examined = new byte[0];
            return CompletableFuture.completedFuture(Collections.emptyList());
        }
        CompletableFuture<Void> room = CompletableFuture.completedFuture(null);
        if (pendingItemLength > 0) {
            // Waiting for the rest of an item: make sure the writer has the credit to send it.
            pipe.ensureCredit(missing);
            if (pendingItemLength > pipe.window) {
                room = pipe.grantExtra(pendingItemLength - pipe.window);
            }
        }
        long have = examined.length;
        return room.thenCompose(v -> pipe.waitForMore(have)).thenCompose(v -> fill(max));
    }
}
