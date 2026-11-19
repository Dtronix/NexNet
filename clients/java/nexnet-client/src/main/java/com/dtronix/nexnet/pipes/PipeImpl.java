package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.session.ControlSource;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionClosedException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.jspecify.annotations.Nullable;

/**
 * One duplex pipe of a session (protocol specification section 7), in either role: opened by this side
 * ({@code initiating}, created with {@code createPipe()} and sent as a pipe argument) or registered for a pipe the
 * server opened. Thread-safe: the reader thread delivers data, credit and state; users read and write from any
 * thread. User-visible futures complete on the callback executor whenever the reader thread triggers them.
 */
final class PipeImpl implements NexusDuplexPipe, ControlSource {
    /** One pending write. */
    private static final class WriteOp {
        final byte[] data;
        int position;
        final CompletableFuture<Void> future = new CompletableFuture<>();

        WriteOp(byte[] data) {
            this.data = data;
        }
    }

    /** A wait for more than {@code have} bytes to be buffered, or for the end of the input. */
    private static final class DataWaiter {
        final long have;
        final CompletableFuture<Void> future = new CompletableFuture<>();

        DataWaiter(long have) {
            this.have = have;
        }
    }

    final PipeManager manager;
    final int localId;
    final boolean initiating;
    final int window;
    private volatile int id;
    private final Object lock = new Object();
    private final Object readLock = new Object();

    // Pipe state: the flags of section 4.6, and the two-sided close of section 7.3.
    private int state;
    private boolean invoked;
    private boolean refused;
    private boolean sentClose;
    private boolean receivedClose;
    private boolean released;
    private boolean freed;
    private boolean closeOnReady;
    private boolean sessionClosed;
    private long closingSince;
    private final CompletableFuture<Void> ready = new CompletableFuture<>();
    private final CompletableFuture<Void> completed = new CompletableFuture<>();

    // Input: buffered data and the receive window. credit is what the peer may still send; consumed bytes are
    // granted back once they reach half the window. Extra credit (for an item larger than the window) is paid off
    // first and never granted back.
    private final ArrayDeque<byte[]> chunks = new ArrayDeque<>();
    private int headOffset;
    private long buffered;
    private long credit;
    private long consumedSinceGrant;
    private long extraOutstanding;
    private long lastDataNanos;
    private long pendingGrant;
    private boolean controlQueued;
    private boolean inputComplete;
    private final ArrayDeque<DataWaiter> dataWaiters = new ArrayDeque<>();
    private CompletableFuture<?> readTail = CompletableFuture.completedFuture(null);

    // Output: credit granted by the peer and the writes waiting for it.
    private long sendCredit;
    private final ArrayDeque<WriteOp> writes = new ArrayDeque<>();
    private boolean outputClosed;
    private @Nullable Throwable outputFailure;
    private boolean outputCompleteRequested;
    private boolean outputCompleteSent;
    private final CompletableFuture<Void> outputCompleted = new CompletableFuture<>();

    PipeImpl(PipeManager manager, int id, int localId, boolean initiating, int window) {
        this.manager = manager;
        this.id = id;
        this.localId = localId;
        this.initiating = initiating;
        this.window = window;
    }

    // ------------------------------------------------------------------ public API

    @Override
    public int id() {
        return id;
    }

    void assignId(int fullId) {
        this.id = fullId;
    }

    @Override
    public CompletableFuture<Void> ready() {
        return ready;
    }

    @Override
    public CompletableFuture<Void> completed() {
        return completed;
    }

    @Override
    public CompletableFuture<@Nullable ByteBuffer> read() {
        synchronized (readLock) {
            CompletableFuture<@Nullable ByteBuffer> next =
                readTail.handle((v, e) -> null).thenCompose(v -> waitForMore(0).thenApply(w -> takeChunk()));
            readTail = next;
            return next;
        }
    }

    @Override
    public CompletableFuture<Void> write(ByteBuffer data) {
        if (data == null) {
            throw new NullPointerException("data");
        }
        byte[] copy = new byte[data.remaining()];
        data.get(copy);
        return writeBytes(copy);
    }

    /** Queues bytes the caller hands over; the array must not change afterwards. */
    CompletableFuture<Void> writeBytes(byte[] bytes) {
        List<Runnable> after = new ArrayList<>();
        CompletableFuture<Void> result;
        synchronized (lock) {
            Throwable failure = writeFailureLocked();
            if (failure != null) {
                result = new CompletableFuture<>();
                result.completeExceptionally(failure);
                return result;
            }
            if (bytes.length == 0) {
                return CompletableFuture.completedFuture(null);
            }
            WriteOp op = new WriteOp(bytes);
            writes.add(op);
            result = op.future;
            pumpLocked(after);
        }
        manager.runAll(after);
        return result;
    }

    private @Nullable Throwable writeFailureLocked() {
        if (sessionClosed) {
            return new SessionClosedException(manager.closeReason());
        }
        if (refused) {
            return new NexusBusyException("The peer refused the pipe.");
        }
        if (outputCompleteRequested || outputClosed || sentClose) {
            return outputFailure != null ? outputFailure : new IllegalStateException("The pipe's output is complete.");
        }
        return null;
    }

    @Override
    public CompletableFuture<Void> completeOutput() {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (!outputCompleteRequested) {
                outputCompleteRequested = true;
                if (sentClose || outputClosed || sessionClosed || refused) {
                    outputCompleteSent = true;
                    after.add(() -> outputCompleted.complete(null));
                } else {
                    pumpLocked(after);
                }
            }
        }
        manager.runAll(after);
        return outputCompleted;
    }

    @Override
    public InputStream inputStream() {
        return new PipeInputStream(this);
    }

    @Override
    public OutputStream outputStream() {
        return new PipeOutputStream(this);
    }

    @Override
    public void close() {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (released) {
                return;
            }
            released = true;
            closeLocked(after);
        }
        manager.runAll(after);
        manager.tryFree(this);
    }

    /**
     * Ends the pipe from this side: discards the input, fails pending writes and sends Complete.
     */
    private void closeLocked(List<Runnable> after) {
        clearInputLocked(after);
        failWritesLocked(after, new IllegalStateException("The pipe was closed."));
        outputClosed = true;
        if (state == 0) {
            if (initiating && invoked && !refused && !sessionClosed) {
                // The server may still answer the invocation: close once its Ready arrives.
                closeOnReady = true;
            } else {
                sentClose = true;
                receivedClose = true;
            }
            after.add(() -> {
                ready.completeExceptionally(new CancellationException("The pipe was closed before it was ready."));
                completed.complete(null);
            });
        } else {
            sendCloseLocked();
            completeLocked(after);
        }
        if (!outputCompleteSent) {
            outputCompleteSent = true;
            after.add(() -> outputCompleted.complete(null));
        }
    }

    /** Resets the pipe after a bad channel item: discards the input and sends Complete. The session stays up. */
    void reset() {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (state == 0 || sentClose) {
                clearInputLocked(after);
            } else {
                closeLocked(after);
            }
        }
        manager.runAll(after);
        manager.tryFree(this);
    }

    private void sendCloseLocked() {
        if (sentClose || sessionClosed) {
            return;
        }
        sentClose = true;
        closingSince = manager.nanoTime();
        state = PipeWire.COMPLETE;
        inputComplete = true;
        outputClosed = true;
        sendStateLocked(PipeWire.COMPLETE, 0);
    }

    private void sendStateLocked(int stateToSend, long grantedWindow) {
        manager.send(Frame.of(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, PipeWire.stateBody(id, stateToSend,
            grantedWindow)));
    }

    // ------------------------------------------------------------------ roles

    /** Initiating side: the pipe is being sent as an invocation argument. False if it can't be. */
    boolean markInvoked() {
        synchronized (lock) {
            if (invoked || released || refused || sessionClosed) {
                return false;
            }
            invoked = true;
            return true;
        }
    }

    /** Initiating side: the invocation was not sent; the pipe fails and is freed. */
    void invocationFailed(Throwable error) {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (state != 0) {
                return;
            }
            refused = true;
            sentClose = true;
            receivedClose = true;
            released = true;
            closeOnReady = false;
            failWritesLocked(after, error);
            after.add(() -> {
                ready.completeExceptionally(error);
                completed.complete(null);
            });
        }
        manager.runAll(after);
        manager.tryFree(this);
    }

    /** Registering side: opens the receive window and sends Ready with it. */
    void sendReady() {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            state = PipeWire.READY;
            credit = window;
            lastDataNanos = manager.nanoTime();
            sendStateLocked(PipeWire.READY, window);
            after.add(() -> ready.complete(null));
        }
        manager.runAll(after);
    }

    // ------------------------------------------------------------------ from the reader thread

    /** Buffers data from the peer. False if it breaks the protocol (beyond the credit, or after the peer closed). */
    boolean onData(byte[] body) {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            int length = body.length;
            if (receivedClose || length > credit) {
                return false;
            }
            credit -= length;
            if (inputComplete) {
                // Discarded, but it still used credit.
                return true;
            }
            if (length > 0) {
                chunks.add(body);
                buffered += length;
                PipeCounters.BUFFERED_BYTES.addAndGet(length);
                lastDataNanos = manager.nanoTime();
                wakeLocked(after);
            }
        }
        manager.runAll(after);
        return true;
    }

    /** Adds send credit granted by the peer. False if the total exceeds the protocol maximum. */
    boolean addSendCredit(long bytes) {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (sendCredit + bytes > PipeWire.MAX_CREDIT) {
                return false;
            }
            sendCredit += bytes;
            pumpLocked(after);
        }
        manager.runAll(after);
        return true;
    }

    /** Handles a state message from the peer; returns a disconnect reason when it breaks the protocol. */
    @Nullable DisconnectReason onRemoteState(int remote, long remoteWindow) {
        List<Runnable> after = new ArrayList<>();
        boolean requestGrant = false;
        synchronized (lock) {
            if ((remote & PipeWire.REFUSED) != 0) {
                if (state == 0 && initiating) {
                    refused = true;
                    sentClose = true;
                    receivedClose = true;
                    released = true;
                    closeOnReady = false;
                    NexusBusyException busy = new NexusBusyException(
                        "The peer refused the pipe: a session limit is reached or the invocation is not authorized.");
                    failWritesLocked(after, busy);
                    after.add(() -> {
                        ready.completeExceptionally(busy);
                        completed.complete(null);
                    });
                }
            } else {
                if (remoteWindow > 0) {
                    if (sendCredit + remoteWindow > PipeWire.MAX_CREDIT) {
                        return DisconnectReason.FLOW_CONTROL;
                    }
                    sendCredit += remoteWindow;
                }
                if (state == 0) {
                    if ((remote & PipeWire.READY) == 0) {
                        return null;
                    }
                    state = PipeWire.READY;
                    lastDataNanos = manager.nanoTime();
                    if (initiating) {
                        // The peer's Ready carried its window; grant ours in return.
                        credit = window;
                        pendingGrant += window;
                        requestGrant = !controlQueued;
                        controlQueued = true;
                    }
                    if (closeOnReady) {
                        sendCloseLocked();
                    } else {
                        after.add(() -> ready.complete(null));
                    }
                }
                int newly = remote & ~state & (PipeWire.CLIENT_READER_COMPLETE | PipeWire.CLIENT_WRITER_COMPLETE);
                if ((newly & PipeWire.CLIENT_READER_COMPLETE) != 0) {
                    // The server finished writing: the input ends after what is buffered.
                    inputComplete = true;
                    wakeLocked(after);
                }
                if ((newly & PipeWire.CLIENT_WRITER_COMPLETE) != 0) {
                    outputClosed = true;
                    outputFailure = new IllegalStateException("The peer completed the pipe's input.");
                    failWritesLocked(after, outputFailure);
                }
                state |= remote & PipeWire.COMPLETE;
                if (remote == PipeWire.COMPLETE && !receivedClose) {
                    receivedClose = true;
                    inputComplete = true;
                    wakeLocked(after);
                    // Answer the peer's close with ours so both sides can free the pipe.
                    sendCloseLocked();
                }
                pumpLocked(after);
                completeLocked(after);
            }
        }
        if (requestGrant) {
            manager.requestControl(this);
        }
        manager.runAll(after);
        return null;
    }

    /** The session ended: everything completes, nothing more is sent. */
    void onSessionClosed(DisconnectReason reason) {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            sessionClosed = true;
            closeOnReady = false;
            inputComplete = true;
            clearInputLocked(after);
            outputClosed = true;
            SessionClosedException error = new SessionClosedException(reason);
            failWritesLocked(after, error);
            if (!outputCompleteSent) {
                outputCompleteSent = true;
                after.add(() -> outputCompleted.complete(null));
            }
            after.add(() -> {
                ready.completeExceptionally(error);
                completed.complete(null);
            });
        }
        manager.runAll(after);
    }

    // ------------------------------------------------------------------ freeing

    /** Marks the pipe freed if both sides closed it and the application released it; true only once. */
    boolean tryMarkFreed(boolean force) {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            if (freed || !(force || (sentClose && receivedClose && released))) {
                return false;
            }
            freed = true;
            clearInputLocked(after);
        }
        manager.runAll(after);
        return true;
    }

    /** Budget reserved for oversized items and not yet paid off; zeroed. */
    long takeExtra() {
        synchronized (lock) {
            long extra = extraOutstanding;
            extraOutstanding = 0;
            return extra;
        }
    }

    // ------------------------------------------------------------------ input (users)

    /** Completes when more than {@code have} bytes are buffered or the input has ended. */
    CompletableFuture<Void> waitForMore(long have) {
        synchronized (lock) {
            if (buffered > have || inputComplete) {
                return CompletableFuture.completedFuture(null);
            }
            DataWaiter waiter = new DataWaiter(have);
            dataWaiters.add(waiter);
            return waiter.future;
        }
    }

    private @Nullable ByteBuffer takeChunk() {
        ByteBuffer result;
        boolean grant;
        synchronized (lock) {
            byte[] chunk = chunks.poll();
            if (chunk == null) {
                return null;
            }
            int length = chunk.length - headOffset;
            result = headOffset == 0 ? ByteBuffer.wrap(chunk) : ByteBuffer.wrap(chunk, headOffset, length).slice();
            headOffset = 0;
            grant = consumedLocked(length);
        }
        if (grant) {
            manager.requestControl(this);
        }
        return result;
    }

    /** The input has ended and everything buffered was read. */
    boolean isInputDrained() {
        synchronized (lock) {
            return inputComplete && buffered == 0;
        }
    }

    /** The input has ended (data may still be buffered). */
    boolean isInputComplete() {
        synchronized (lock) {
            return inputComplete;
        }
    }

    /** The output can no longer be written: this side completed it, the peer completed its input, or it closed. */
    boolean isOutputClosed() {
        synchronized (lock) {
            return outputClosed || outputCompleteRequested || sentClose || sessionClosed;
        }
    }

    /** Copies the buffered bytes after the first {@code skip} without consuming them. */
    byte[] peek(long skip) {
        synchronized (lock) {
            long available = buffered - skip;
            if (available <= 0) {
                return new byte[0];
            }
            byte[] out = new byte[(int) available];
            long toSkip = skip;
            int written = 0;
            boolean first = true;
            for (byte[] chunk : chunks) {
                int start = first ? headOffset : 0;
                first = false;
                int length = chunk.length - start;
                if (toSkip >= length) {
                    toSkip -= length;
                    continue;
                }
                start += (int) toSkip;
                length -= (int) toSkip;
                toSkip = 0;
                System.arraycopy(chunk, start, out, written, length);
                written += length;
            }
            return out;
        }
    }

    /** Consumes {@code count} buffered bytes, granting credit back as the rules say. */
    void consume(long count) {
        boolean grant;
        synchronized (lock) {
            long left = Math.min(count, buffered);
            long done = left;
            while (left > 0) {
                byte[] head = chunks.peek();
                int length = head.length - headOffset;
                if (left >= length) {
                    chunks.poll();
                    headOffset = 0;
                    left -= length;
                } else {
                    headOffset += (int) left;
                    left = 0;
                }
            }
            grant = consumedLocked(done);
        }
        if (grant) {
            manager.requestControl(this);
        }
    }

    /** Records consumed bytes; true when a grant must be requested. */
    private boolean consumedLocked(long count) {
        if (count <= 0) {
            return false;
        }
        buffered -= count;
        PipeCounters.BUFFERED_BYTES.addAndGet(-count);
        long remaining = count;
        if (extraOutstanding > 0) {
            long paid = Math.min(remaining, extraOutstanding);
            extraOutstanding -= paid;
            remaining -= paid;
            manager.releaseExtra(paid);
        }
        if (remaining <= 0 || inputComplete) {
            return false;
        }
        consumedSinceGrant += remaining;
        if (consumedSinceGrant < window / 2) {
            return false;
        }
        return grantConsumedLocked();
    }

    private boolean grantConsumedLocked() {
        long grant = consumedSinceGrant;
        consumedSinceGrant = 0;
        credit += grant;
        pendingGrant += grant;
        if (controlQueued) {
            return false;
        }
        controlQueued = true;
        return true;
    }

    /**
     * Waiting for the rest of an item: if the writer's remaining credit can't cover {@code missing} bytes, grants
     * everything consumed but not yet granted back.
     */
    void ensureCredit(long missing) {
        boolean grant;
        synchronized (lock) {
            if (credit >= missing || consumedSinceGrant <= 0 || inputComplete) {
                return;
            }
            grant = grantConsumedLocked();
        }
        if (grant) {
            manager.requestControl(this);
        }
    }

    /**
     * Makes room for an item larger than the window: reserves {@code extra} bytes from the session budget (FIFO) and
     * grants them as extra credit. Does nothing while earlier extra credit is outstanding.
     */
    CompletableFuture<Void> grantExtra(long extra) {
        synchronized (lock) {
            if (extraOutstanding > 0 || inputComplete || extra <= 0) {
                return CompletableFuture.completedFuture(null);
            }
        }
        return manager.reserveExtra(extra).thenAccept(reserved -> {
            if (!reserved) {
                return;
            }
            boolean grant;
            synchronized (lock) {
                if (inputComplete || freed) {
                    grant = false;
                    manager.releaseExtra(extra);
                } else {
                    extraOutstanding += extra;
                    credit += extra;
                    lastDataNanos = manager.nanoTime();
                    pendingGrant += extra;
                    grant = !controlQueued;
                    controlQueued = true;
                }
            }
            if (grant) {
                manager.requestControl(this);
            }
        });
    }

    @Override
    public void appendControlFrames(List<Frame> frames) {
        synchronized (lock) {
            controlQueued = false;
            long grant = pendingGrant;
            pendingGrant = 0;
            if (grant > 0 && !sentClose && !sessionClosed && id != 0) {
                frames.add(Frame.of(MessageTypes.PIPE_WINDOW_UPDATE, id, PipeWire.windowBody(grant), 0, 4));
            }
        }
    }

    // ------------------------------------------------------------------ diagnostics

    /** Describes a stall for the log, or null. */
    @Nullable String describeStall(long nowNanos, long timeoutNanos) {
        synchronized (lock) {
            if (sentClose && !receivedClose && !sessionClosed && nowNanos - closingSince > timeoutNanos) {
                return "pipe P" + id + " closing, the peer has not confirmed for "
                    + TimeUnit.NANOSECONDS.toMillis(nowNanos - closingSince) + " ms";
            }
            if (extraOutstanding > 0 && !inputComplete && nowNanos - lastDataNanos > timeoutNanos) {
                return "pipe P" + id + " has " + extraOutstanding + " bytes of extra credit outstanding and no data for "
                    + TimeUnit.NANOSECONDS.toMillis(nowNanos - lastDataNanos) + " ms";
            }
            return null;
        }
    }

    /** Bytes buffered and not yet read; for tests. */
    long bufferedBytes() {
        synchronized (lock) {
            return buffered;
        }
    }

    /** Send credit held; for tests. */
    long sendCredit() {
        synchronized (lock) {
            return sendCredit;
        }
    }

    // ------------------------------------------------------------------ helpers (lock held)

    private void pumpLocked(List<Runnable> after) {
        if ((state & PipeWire.READY) == 0 || id == 0 || sessionClosed) {
            return;
        }
        while (!writes.isEmpty() && sendCredit > 0 && !sentClose && !outputClosed) {
            WriteOp op = writes.peek();
            int length = (int) Math.min(Math.min(op.data.length - op.position, sendCredit), Frame.MAX_BODY_LENGTH);
            manager.send(Frame.of(MessageTypes.DUPLEX_PIPE_WRITE, id, op.data, op.position, length));
            sendCredit -= length;
            op.position += length;
            if (op.position == op.data.length) {
                writes.poll();
                after.add(() -> op.future.complete(null));
            }
        }
        if (!writes.isEmpty() && (sentClose || outputClosed)) {
            failWritesLocked(after, outputFailure != null ? outputFailure
                : new IllegalStateException("The pipe's output is complete."));
        }
        if (writes.isEmpty() && outputCompleteRequested && !outputCompleteSent) {
            outputCompleteSent = true;
            if (!sentClose && (state & PipeWire.CLIENT_WRITER_COMPLETE) == 0) {
                state |= PipeWire.CLIENT_WRITER_COMPLETE;
                if (state == PipeWire.COMPLETE) {
                    // Both directions are now complete: like the C# side, this state is this side's close.
                    sentClose = true;
                    closingSince = manager.nanoTime();
                    inputComplete = true;
                }
                sendStateLocked(state, 0);
                completeLocked(after);
            }
            after.add(() -> outputCompleted.complete(null));
        }
    }

    private void failWritesLocked(List<Runnable> after, Throwable error) {
        while (!writes.isEmpty()) {
            WriteOp op = writes.poll();
            after.add(() -> op.future.completeExceptionally(error));
        }
    }

    private void clearInputLocked(List<Runnable> after) {
        inputComplete = true;
        if (buffered > 0) {
            PipeCounters.BUFFERED_BYTES.addAndGet(-buffered);
        }
        buffered = 0;
        chunks.clear();
        headOffset = 0;
        wakeLocked(after);
    }

    private void wakeLocked(List<Runnable> after) {
        Iterator<DataWaiter> it = dataWaiters.iterator();
        while (it.hasNext()) {
            DataWaiter waiter = it.next();
            if (buffered > waiter.have || inputComplete) {
                it.remove();
                after.add(() -> waiter.future.complete(null));
            }
        }
    }

    private void completeLocked(List<Runnable> after) {
        if ((state & PipeWire.COMPLETE) == PipeWire.COMPLETE) {
            after.add(() -> completed.complete(null));
        }
    }
}
