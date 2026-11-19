package com.dtronix.nexnet.session;

import java.util.ArrayDeque;
import java.util.List;

/**
 * Coalesces the small control messages the session sends on its own behalf (pings, returned invocation credit,
 * invocation cancellations, large-message aborts, pipe control), so the reader thread and the timer never wait on a
 * send. Requests only record what is pending and wake the writer thread, which drains them after the queued frames
 * of each batch. Like the C# {@code SessionControlSender}: at most one ping is pending at a time, and invocation
 * credit is returned once half the advertised credits are pending.
 */
public final class ControlSender {
    private final Object lock = new Object();
    private final Runnable wake;
    private final int creditThreshold;
    private boolean pingPending;
    private int pendingCredit;
    private final ArrayDeque<Integer> cancellations = new ArrayDeque<>();
    private final ArrayDeque<int[]> aborts = new ArrayDeque<>();
    private final ArrayDeque<ControlSource> sources = new ArrayDeque<>();

    ControlSender(Runnable wake, int advertisedInvocationCredits) {
        this.wake = wake;
        this.creditThreshold = Math.max(1, advertisedInvocationCredits / 2);
    }

    /** Requests a {@code Ping}. Any number of requests before the next drain send one. */
    public void requestPing() {
        synchronized (lock) {
            pingPending = true;
        }
        wake.run();
    }

    /**
     * Returns invocation credit to the server, sent once the pending credit reaches half the advertised credits.
     *
     * @param credits the credits, at least 1
     */
    public void returnInvocationCredit(int credits) {
        if (credits < 1) {
            throw new IllegalArgumentException("credits");
        }
        boolean due;
        synchronized (lock) {
            pendingCredit += credits;
            due = pendingCredit >= creditThreshold;
        }
        if (due) {
            wake.run();
        }
    }

    /**
     * Requests an {@code InvocationCancellation}.
     *
     * @param invocationId the invocation id
     */
    public void requestInvocationCancellation(int invocationId) {
        synchronized (lock) {
            cancellations.add(invocationId & 0xFFFF);
        }
        wake.run();
    }

    /**
     * Requests a {@code MessageAbort}.
     *
     * @param invocationId the invocation id of the stream
     * @param kind 0 argument, 1 result
     * @param reason 0 Busy, 1 TooLarge, 2 Cancelled, 3 Ack
     */
    public void requestAbort(int invocationId, int kind, int reason) {
        synchronized (lock) {
            aborts.add(new int[] {invocationId & 0xFFFF, kind, reason});
        }
        wake.run();
    }

    /**
     * Requests the pending control frames of a source. The source is asked once per request.
     *
     * @param source the source
     */
    public void requestControl(ControlSource source) {
        synchronized (lock) {
            sources.add(source);
        }
        wake.run();
    }

    boolean hasPending() {
        synchronized (lock) {
            return pingPending || pendingCredit >= creditThreshold || !cancellations.isEmpty() || !aborts.isEmpty()
                || !sources.isEmpty();
        }
    }

    /** Called on the writer thread: appends the pending control frames and clears them. */
    void drainTo(List<Frame> frames) {
        ControlSource[] toAsk;
        synchronized (lock) {
            if (pingPending) {
                pingPending = false;
                frames.add(Frame.headerOnly(MessageTypes.PING));
            }
            for (int[] abort : aborts) {
                WireWriter w = new WireWriter(8);
                w.writeArrayHeader(3);
                w.writeInt(abort[0]);
                w.writeInt(abort[1]);
                w.writeInt(abort[2]);
                frames.add(Frame.of(MessageTypes.MESSAGE_ABORT, w.toByteArray()));
            }
            aborts.clear();
            for (int id : cancellations) {
                WireWriter w = new WireWriter(4);
                w.writeArrayHeader(1);
                w.writeInt(id);
                frames.add(Frame.of(MessageTypes.INVOCATION_CANCELLATION, w.toByteArray()));
            }
            cancellations.clear();
            if (pendingCredit >= creditThreshold) {
                while (pendingCredit > 0) {
                    int credits = Math.min(pendingCredit, 0xFFFF);
                    pendingCredit -= credits;
                    WireWriter w = new WireWriter(4);
                    w.writeArrayHeader(1);
                    w.writeInt(credits);
                    frames.add(Frame.of(MessageTypes.INVOCATION_CREDIT, w.toByteArray()));
                }
            }
            toAsk = sources.toArray(new ControlSource[0]);
            sources.clear();
        }
        for (ControlSource source : toAsk) {
            source.appendControlFrames(frames);
        }
    }
}
