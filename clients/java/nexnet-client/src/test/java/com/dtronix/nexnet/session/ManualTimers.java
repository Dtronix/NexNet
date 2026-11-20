package com.dtronix.nexnet.session;

import com.dtronix.nexnet.Registration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;

/** A manual clock: tasks run on the thread that calls {@link #advance(long)}. */
public final class ManualTimers implements SessionTimers {
    private final Object lock = new Object();
    private final List<long[]> due = new ArrayList<>();
    private final List<Runnable> tasks = new ArrayList<>();
    private long now = 1_000_000_000L;
    private long sequence;

    @Override
    public long nanoTime() {
        synchronized (lock) {
            return now;
        }
    }

    @Override
    public Registration schedule(Runnable task, long delayMillis) {
        long[] entry;
        synchronized (lock) {
            entry = new long[] {now + TimeUnit.MILLISECONDS.toNanos(Math.max(0, delayMillis)), sequence++};
            due.add(entry);
            tasks.add(task);
        }
        return () -> {
            synchronized (lock) {
                int index = due.indexOf(entry);
                if (index >= 0) {
                    due.remove(index);
                    tasks.remove(index);
                }
            }
        };
    }

    /** Scheduled tasks not yet run. */
    public int pending() {
        synchronized (lock) {
            return due.size();
        }
    }

    /**
     * Waits until the session's writer thread has finished its last transport write. The writer clears its
     * write-in-progress flag ({@link NexusSession#isFlushing()}) only after the flush returns, so the peer can read the
     * bytes before that. A test that then advances this clock past the progress timeout while the flag is still set
     * makes the keepalive tick see a stalled write (specification section 9), which closes the session without the
     * disconnect byte. Call this before such an advance, once nothing else is being written.
     *
     * @param session the session
     * @throws InterruptedException when interrupted while waiting
     */
    public static void awaitWriterIdle(NexusSession session) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (session.isFlushing()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("The session's writer did not finish its write.");
            }
            Thread.sleep(1);
        }
    }

    /** Moves the clock forward, running every task that falls due, in time order. */
    public void advance(long millis) {
        long target;
        synchronized (lock) {
            target = now + TimeUnit.MILLISECONDS.toNanos(millis);
        }
        while (true) {
            Runnable next = null;
            synchronized (lock) {
                int best = -1;
                for (int i = 0; i < due.size(); i++) {
                    long[] e = due.get(i);
                    if (e[0] <= target && (best < 0 || e[0] < due.get(best)[0]
                        || (e[0] == due.get(best)[0] && e[1] < due.get(best)[1]))) {
                        best = i;
                    }
                }
                if (best >= 0) {
                    now = Math.max(now, due.get(best)[0]);
                    due.remove(best);
                    next = tasks.remove(best);
                } else {
                    now = target;
                }
            }
            if (next == null) {
                return;
            }
            next.run();
        }
    }
}
