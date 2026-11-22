package com.dtronix.nexnet.interop.host;

import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;

/**
 * A slow consumer installed by {@code list.throttle} (control protocol §6.10): the process test hook of one list
 * ({@code NexusListClient.setProcessTestHook}), counting the messages it sees from its installation.
 */
final class ListThrottle implements Supplier<CompletionStage<?>> {
    /** The longest a throttle with {@code pause} holds a message, so a failed test never leaves a reader stalled. */
    static final long MAX_HOLD_MS = 60_000;

    private final ScheduledExecutorService timer;
    private final int every;
    private final int delayMs;
    private final int firstDelayMs;
    private final boolean pause;
    private final Object lock = new Object();
    private int seen;
    private CompletableFuture<Void> held;
    private boolean stopped;

    ListThrottle(ScheduledExecutorService timer, int every, int delayMs, int firstDelayMs, boolean pause) {
        this.timer = timer;
        this.every = every;
        this.delayMs = delayMs;
        this.firstDelayMs = firstDelayMs;
        this.pause = pause;
    }

    /** Whether the throttle does anything: a delay or a pause. */
    static boolean isActive(int delayMs, int firstDelayMs, boolean pause) {
        return delayMs > 0 || firstDelayMs > 0 || pause;
    }

    /** Called on the list's processing lane before each message; never blocks. */
    @Override
    public CompletionStage<?> get() {
        int n;
        synchronized (lock) {
            n = ++seen;
            if (stopped) {
                return null;
            }
            if (n == 1 && pause) {
                CompletableFuture<Void> hold = new CompletableFuture<>();
                ScheduledFuture<?> cap = timer.schedule(() -> hold.complete(null), MAX_HOLD_MS, TimeUnit.MILLISECONDS);
                hold.whenComplete((v, e) -> {
                    cap.cancel(false);
                    synchronized (lock) {
                        if (held == hold) {
                            held = null;
                        }
                    }
                });
                held = hold;
                return hold;
            }
        }
        if (n == 1 && firstDelayMs > 0) {
            return delay(firstDelayMs);
        }
        if (delayMs > 0 && n % every == 0) {
            return delay(delayMs);
        }
        return null;
    }

    private CompletableFuture<Void> delay(long ms) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        timer.schedule(() -> done.complete(null), ms, TimeUnit.MILLISECONDS);
        return done;
    }

    /** The messages the hook was called for. */
    int seen() {
        synchronized (lock) {
            return seen;
        }
    }

    /** Whether the hook holds a message ({@code pause}). */
    boolean holding() {
        synchronized (lock) {
            return held != null;
        }
    }

    /** Releases a held message; later messages are neither held nor delayed. */
    void stop() {
        CompletableFuture<Void> release;
        synchronized (lock) {
            stopped = true;
            release = held;
        }
        if (release != null) {
            release.complete(null);
        }
    }
}
