package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.diagnostics.NexusDirection;
import com.dtronix.nexnet.diagnostics.NexusLargeMessageKind;
import com.dtronix.nexnet.diagnostics.NexusLargeMessageSnapshot;
import com.dtronix.nexnet.session.SessionProtocolException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;
import org.jspecify.annotations.Nullable;

/**
 * Reassembles the large arguments and results the server streams to this client (protocol specification section 6).
 * Called on the reader thread only, except {@link #clear()} and {@link #findStalled(long, long)}. A stream is keyed by (invocation id, kind); its total
 * length is reserved from the session budget at {@code MessageStart} and memory is allocated as chunks arrive. A
 * stream that does not fit the budget is refused with {@code MessageAbort(Busy)} and its chunks are discarded.
 */
final class LargeMessageReceiver {
    /** What the receiver needs from the runtime. */
    interface Owner {
        /** Whether a call with this id is waiting for its result. */
        boolean isCallOutstanding(int invocationId);

        /** Takes one inbound invocation credit for a large argument; false when the server has none left. */
        boolean tryTakeInvocationCredit();

        /** Returns a credit taken for an argument that will not be queued. */
        void returnInvocationCredit();

        /** Queues an abort (never waits). */
        void sendAbort(int invocationId, int kind, int reason);

        /** The reassembled inner message: an {@code Invocation} or {@code InvocationResult} body. */
        void onComplete(int kind, byte[] body, long reserved);
    }

    private static final int SEGMENT = 64 * 1024;

    private static final class Stream {
        final int total;
        final int kind;
        volatile int received;
        volatile long lastProgress;
        boolean aborting;
        boolean reserved;
        byte[] data = new byte[0];

        Stream(int total, int kind, long now) {
            this.total = total;
            this.kind = kind;
            this.lastProgress = now;
        }

        void append(byte[] payload) {
            int needed = received + payload.length;
            if (needed > data.length) {
                int capacity = (int) Math.min(total, Math.max((long) needed, Math.max(SEGMENT, 2L * data.length)));
                data = Arrays.copyOf(data, capacity);
            }
            System.arraycopy(payload, 0, data, received, payload.length);
        }
    }

    private final Owner owner;
    private final SessionBudget budget;
    private final int maxMessageSize;
    private final LongSupplier clock;
    private final Map<Integer, Stream> streams = new HashMap<>();

    LargeMessageReceiver(Owner owner, SessionBudget budget, int maxMessageSize, LongSupplier clock) {
        this.owner = owner;
        this.budget = budget;
        this.maxMessageSize = maxMessageSize;
        this.clock = clock;
    }

    private static int key(int invocationId, int kind) {
        return invocationId | (kind << 16);
    }

    private static SessionProtocolException protocolError(String message) {
        return new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR, message);
    }

    /** Handles a {@code MessageStart}. */
    void onStart(int invocationId, int kind, int innerType, long totalLength) {
        int expectedInner = kind == InvocationRuntime.KIND_ARGUMENT ? InvocationRuntime.INNER_INVOCATION
            : kind == InvocationRuntime.KIND_RESULT ? InvocationRuntime.INNER_RESULT : -1;
        if (expectedInner < 0 || innerType != expectedInner || totalLength == 0) {
            throw protocolError("MessageStart with kind " + kind + ", inner type " + innerType + " and length "
                + totalLength + ".");
        }
        if (totalLength > maxMessageSize) {
            throw protocolError("MessageStart of " + totalLength + " bytes; the advertised limit is "
                + maxMessageSize + ".");
        }
        int key = key(invocationId, kind);
        Stream stream;
        synchronized (streams) {
            if (streams.containsKey(key)) {
                throw protocolError("MessageStart for an active stream (invocation " + invocationId + ").");
            }
        }
        if (kind == InvocationRuntime.KIND_RESULT && !owner.isCallOutstanding(invocationId)) {
            throw protocolError("A result stream for invocation " + invocationId + ", which is not waiting.");
        }
        if (kind == InvocationRuntime.KIND_ARGUMENT && !owner.tryTakeInvocationCredit()) {
            throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL,
                "A large argument received without invocation credit.");
        }
        stream = new Stream((int) totalLength, kind, clock.getAsLong());
        if (budget.tryReserve(totalLength)) {
            stream.reserved = true;
        } else {
            // No room now: refuse it and discard its chunks. The argument will not be queued, so its credit returns.
            stream.aborting = true;
            if (kind == InvocationRuntime.KIND_ARGUMENT) {
                owner.returnInvocationCredit();
            }
            owner.sendAbort(invocationId, kind, InvocationRuntime.ABORT_BUSY);
        }
        synchronized (streams) {
            streams.put(key, stream);
        }
        InvocationCounters.LARGE_MESSAGES.incrementAndGet();
    }

    /** Handles a {@code MessageChunk}. */
    void onChunk(int invocationId, int kind, byte[] payload) {
        int key = key(invocationId, kind);
        Stream stream;
        synchronized (streams) {
            stream = streams.get(key);
        }
        if (stream == null) {
            throw protocolError("A chunk for invocation " + invocationId + ", which has no stream.");
        }
        if ((long) stream.received + payload.length > stream.total) {
            throw protocolError("A chunk past the end of the stream of invocation " + invocationId + ".");
        }
        stream.lastProgress = clock.getAsLong();
        if (stream.aborting) {
            // Discarded, up to the total length; then the key closes.
            stream.received += payload.length;
            if (stream.received == stream.total) {
                remove(key, stream);
            }
            return;
        }
        stream.append(payload);
        stream.received += payload.length;
        if (stream.received < stream.total) {
            return;
        }
        remove(key, stream);
        // The reservation now covers the message; the owner releases it once the message has been handled.
        stream.reserved = false;
        byte[] body = stream.data.length == stream.total ? stream.data : Arrays.copyOf(stream.data, stream.total);
        stream.data = new byte[0];
        owner.onComplete(kind, body, stream.total);
    }

    /**
     * Handles an abort from the stream's sender: {@code Cancelled} ends the stream, {@code Ack} ends a stream this
     * side refused. An abort for an unknown stream is ignored, as it can race with completion.
     */
    void onAbort(int invocationId, int kind, int reason) {
        int key = key(invocationId, kind);
        Stream stream;
        synchronized (streams) {
            stream = streams.get(key);
            if (stream == null || (reason == InvocationRuntime.ABORT_ACK && !stream.aborting)) {
                return;
            }
            streams.remove(key);
        }
        InvocationCounters.LARGE_MESSAGES.decrementAndGet();
        // An argument this side had accepted will not be queued now.
        if (kind == InvocationRuntime.KIND_ARGUMENT && !stream.aborting) {
            owner.returnInvocationCredit();
        }
        close(stream);
    }

    /**
     * Describes a stream that received nothing for longer than the timeout (specification section 9), in the words
     * of the C# {@code FindStalledStream}; null when none did. Called on the timer thread.
     *
     * @param nowNanos the session clock
     * @param timeoutNanos the progress timeout
     * @return the description, or null
     */
    @Nullable String findStalled(long nowNanos, long timeoutNanos) {
        synchronized (streams) {
            for (Map.Entry<Integer, Stream> entry : streams.entrySet()) {
                Stream stream = entry.getValue();
                long idle = nowNanos - stream.lastProgress;
                if (idle > timeoutNanos) {
                    return "inbound large " + (stream.kind == InvocationRuntime.KIND_ARGUMENT ? "argument" : "result")
                        + " of invocation " + (entry.getKey() & 0xFFFF) + ", " + stream.received + " of "
                        + stream.total + " bytes, no data for " + TimeUnit.NANOSECONDS.toMillis(idle) + " ms";
                }
            }
        }
        return null;
    }

    /** Adds a snapshot of every stream being received. */
    void addSnapshots(List<NexusLargeMessageSnapshot> snapshots, long nowNanos) {
        synchronized (streams) {
            for (Map.Entry<Integer, Stream> entry : streams.entrySet()) {
                Stream stream = entry.getValue();
                snapshots.add(new NexusLargeMessageSnapshot(NexusDirection.INBOUND,
                    stream.kind == InvocationRuntime.KIND_ARGUMENT ? NexusLargeMessageKind.ARGUMENT
                        : NexusLargeMessageKind.RESULT,
                    entry.getKey() & 0xFFFF, stream.total, stream.received,
                    InvocationRuntime.since(nowNanos, stream.lastProgress), stream.aborting));
            }
        }
    }

    /** The streams being received. */
    int count() {
        synchronized (streams) {
            return streams.size();
        }
    }

    /** Drops every stream when the session ends. */
    void clear() {
        List<Stream> dropped;
        synchronized (streams) {
            dropped = new ArrayList<>(streams.values());
            streams.clear();
        }
        for (Stream stream : dropped) {
            InvocationCounters.LARGE_MESSAGES.decrementAndGet();
            close(stream);
        }
    }

    private void remove(int key, Stream stream) {
        boolean removed;
        synchronized (streams) {
            removed = streams.remove(key, stream);
        }
        if (removed) {
            InvocationCounters.LARGE_MESSAGES.decrementAndGet();
        }
    }

    private void close(Stream stream) {
        stream.data = new byte[0];
        if (stream.reserved) {
            stream.reserved = false;
            budget.release(stream.total);
        }
    }
}
