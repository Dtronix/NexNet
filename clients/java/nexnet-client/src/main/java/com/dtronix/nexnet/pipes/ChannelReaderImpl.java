package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.Function;
import org.jspecify.annotations.Nullable;

/**
 * Reads channel items from a pipe (protocol specification section 7.4): each item is an unsigned LEB128 length
 * followed by exactly one MessagePack value of that length. Items are consumed from the pipe (and so credited back to
 * the writer) only once they are decoded and the read has claimed them, so a canceled read consumes nothing. An item
 * that breaks the framing resets the pipe; the session stays up. Reads are served one at a time, in call order.
 */
final class ChannelReaderImpl<T> implements NexusChannelReader<T> {
    private static final int PENDING = 0;
    private static final int CLAIMED = 1;
    private static final int CANCELED = 2;

    private final PipeImpl pipe;
    private final Formatter<T> formatter;
    private final PipeManager manager;
    private final Object lock = new Object();
    /** Completes when the last read queued has ended; the next read starts after it. */
    private CompletableFuture<Void> tail = CompletableFuture.completedFuture(null);
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
        return read(CancellationToken.NONE);
    }

    @Override
    public CompletableFuture<Optional<T>> read(CancellationToken cancellationToken) {
        return start(new ReadOp<>(1, false, items -> {
            if (items.isEmpty()) {
                return Optional.<T>empty();
            }
            T value = items.get(0);
            if (value == null) {
                // An empty optional would read as the end of the channel.
                throw new NullPointerException("The channel item is null; use readItem() for a nullable item type.");
            }
            return Optional.of(value);
        }), cancellationToken);
    }

    @Override
    public CompletableFuture<ChannelReadResult<T>> readItem() {
        return readItem(CancellationToken.NONE);
    }

    @Override
    public CompletableFuture<ChannelReadResult<T>> readItem(CancellationToken cancellationToken) {
        return start(new ReadOp<>(1, false, items -> items.isEmpty()
            ? ChannelReadResult.<T>completed() : ChannelReadResult.of(items.get(0))), cancellationToken);
    }

    @Override
    public CompletableFuture<List<T>> readBatch(int max) {
        return readBatch(max, CancellationToken.NONE);
    }

    @Override
    public CompletableFuture<List<T>> readBatch(int max, CancellationToken cancellationToken) {
        if (max < 1) {
            throw new IllegalArgumentException("max must be at least 1");
        }
        return start(new ReadOp<>(max, false, items -> items), cancellationToken);
    }

    @Override
    public CompletableFuture<List<T>> readUntilComplete() {
        return start(new ReadOp<>(Integer.MAX_VALUE, true, items -> items), CancellationToken.NONE);
    }

    /** Queues {@code op} behind the reads before it. */
    private <R> CompletableFuture<R> start(ReadOp<R> op, CancellationToken token) {
        if (token == null) {
            throw new NullPointerException("cancellationToken");
        }
        if (token.isCancellationRequested()) {
            // Canceled before it starts: nothing is read (D17).
            op.cancel(false);
            return op;
        }
        if (token.canBeCanceled()) {
            op.registration = token.register(() -> op.cancel(false));
        }
        CompletableFuture<Void> previous;
        synchronized (lock) {
            previous = tail;
            tail = op.done;
        }
        previous.whenComplete((v, e) -> step(op));
        return op;
    }

    /** Runs {@code op} until it delivers, fails, waits for data or finds itself canceled. */
    private void step(ReadOp<?> op) {
        try {
            stepUnchecked(op);
        } catch (RuntimeException e) {
            op.fail(e);
        }
    }

    private void stepUnchecked(ReadOp<?> op) {
        while (true) {
            if (op.isCanceledRead()) {
                op.finish();
                return;
            }
            if (failed) {
                if (op.claim()) {
                    op.deliver(Collections.emptyList());
                } else {
                    op.finish();
                }
                return;
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
                while (items.size() < op.max && position < examined.length) {
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
                pipe.setPartialItem(0);
                manager.recordReset(pipe.id(), e instanceof NexusMessageTooLargeException);
                pipe.reset();
                op.fail(e);
                return;
            }
            if (!items.isEmpty() && !op.claim()) {
                // Canceled while decoding: the items stay in the pipe for the next read.
                op.finish();
                return;
            }
            // Diagnostics: the item that has partly arrived, if any (its start is at the head of the pipe's buffer).
            pipe.setPartialItem(pendingItemLength);
            if (position > 0) {
                pipe.consume(position);
                examined = Arrays.copyOfRange(examined, position, examined.length);
            }
            if (!items.isEmpty()) {
                if (op.all != null) {
                    op.all.addAll(items);
                    continue;
                }
                op.deliver(items);
                return;
            }
            if (ended) {
                // An incomplete item at the end is dropped, as in C#.
                examined = new byte[0];
                if (op.claim()) {
                    op.deliver(op.all != null ? op.all : Collections.<T>emptyList());
                } else {
                    op.finish();
                }
                return;
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
            room.whenComplete((v, e) -> {
                if (e != null) {
                    op.fail(e);
                } else {
                    waitThenStep(op, have);
                }
            });
            return;
        }
    }

    /** Waits for more than {@code have} bytes (or the end of input), then steps; a cancel stops the wait. */
    private void waitThenStep(ReadOp<?> op, long have) {
        if (op.isCanceledRead()) {
            op.finish();
            return;
        }
        CompletableFuture<Void> wait = pipe.waitForMore(have);
        if (!op.setWaiting(wait)) {
            pipe.abandonWait(wait);
        }
        wait.whenComplete((v, e) -> {
            op.clearWaiting(wait);
            step(op);
        });
    }

    /**
     * One read: the future the caller gets. Its status moves from pending to claimed (it takes items) or to canceled
     * exactly once, so a cancel and a delivery never both happen.
     */
    private final class ReadOp<R> extends CompletableFuture<R> {
        final int max;
        /** readUntilComplete: the items so far. Such a read is not canceled by cancelling its future. */
        final @Nullable List<T> all;
        final Function<List<T>, R> map;
        final AtomicInteger status = new AtomicInteger(PENDING);
        /** Completes when this read has ended, delivered or not; the next read starts then. */
        final CompletableFuture<Void> done = new CompletableFuture<>();
        volatile Registration registration = Registration.NONE;
        private @Nullable CompletableFuture<Void> waiting;

        ReadOp(int max, boolean untilComplete, Function<List<T>, R> map) {
            this.max = max;
            this.all = untilComplete ? new ArrayList<>() : null;
            this.map = map;
        }

        @Override
        public boolean cancel(boolean mayInterruptIfRunning) {
            if (all != null) {
                return super.cancel(mayInterruptIfRunning);
            }
            if (!status.compareAndSet(PENDING, CANCELED)) {
                return isCancelled();
            }
            boolean canceled = super.cancel(mayInterruptIfRunning);
            CompletableFuture<Void> wait;
            synchronized (this) {
                wait = waiting;
                waiting = null;
            }
            if (wait != null) {
                // Its continuation sees the cancel and ends the read.
                pipe.abandonWait(wait);
            }
            return canceled;
        }

        boolean isCanceledRead() {
            return status.get() == CANCELED;
        }

        /** Takes the items for this read; false when it was canceled first. */
        boolean claim() {
            return all != null || status.compareAndSet(PENDING, CLAIMED);
        }

        /** Records the wait in progress; false when the read was canceled first. */
        synchronized boolean setWaiting(CompletableFuture<Void> wait) {
            if (status.get() == CANCELED) {
                return false;
            }
            waiting = wait;
            return true;
        }

        synchronized void clearWaiting(CompletableFuture<Void> wait) {
            if (waiting == wait) {
                waiting = null;
            }
        }

        /** Completes the claimed read with {@code items}. */
        void deliver(List<T> items) {
            try {
                R result = map.apply(items);
                super.complete(result);
            } catch (RuntimeException e) {
                super.completeExceptionally(e);
            }
            finish();
        }

        void fail(Throwable error) {
            if (claim()) {
                super.completeExceptionally(error);
            }
            finish();
        }

        void finish() {
            registration.close();
            done.complete(null);
        }
    }
}
