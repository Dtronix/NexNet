package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.SessionTimers;
import java.util.ArrayList;
import java.util.List;
import java.util.PriorityQueue;
import java.util.concurrent.TimeUnit;

/**
 * A manual clock ({@code ClientSpec.fakeTime}): time moves only by {@link #advance}, which runs the timers that fell
 * due, in order, before it returns. Timers with no delay run at once on the runtime's pool, as with the system timers.
 */
final class FakeTimers implements SessionTimers {
    private final Object lock = new Object();
    private final long base = System.nanoTime();
    private final PriorityQueue<Task> queue = new PriorityQueue<>();
    private long offsetNanos;
    private long sequence;

    private static final class Task implements Comparable<Task> {
        final long due;
        final long order;
        final Runnable run;
        volatile boolean canceled;

        Task(long due, long order, Runnable run) {
            this.due = due;
            this.order = order;
            this.run = run;
        }

        @Override
        public int compareTo(Task other) {
            int byDue = Long.compare(due, other.due);
            return byDue != 0 ? byDue : Long.compare(order, other.order);
        }
    }

    @Override
    public long nanoTime() {
        synchronized (lock) {
            return base + offsetNanos;
        }
    }

    @Override
    public Registration schedule(Runnable task, long delayMillis) {
        if (delayMillis <= 0) {
            Task now = new Task(0, 0, task);
            NexusExecutors.execute(NexusExecutors.shared(), () -> {
                if (!now.canceled) {
                    task.run();
                }
            });
            return () -> now.canceled = true;
        }
        Task scheduled;
        synchronized (lock) {
            scheduled = new Task(base + offsetNanos + TimeUnit.MILLISECONDS.toNanos(delayMillis), sequence++, task);
            queue.add(scheduled);
        }
        return () -> {
            scheduled.canceled = true;
            synchronized (lock) {
                queue.remove(scheduled);
            }
        };
    }

    /**
     * Moves the clock and runs every timer due, including those the timers schedule within the window.
     *
     * @param millis how far to move
     * @return how many timers ran
     */
    int advance(long millis) {
        long target;
        synchronized (lock) {
            target = base + offsetNanos + TimeUnit.MILLISECONDS.toNanos(Math.max(0, millis));
        }
        int ran = 0;
        while (true) {
            Task next;
            synchronized (lock) {
                next = queue.peek();
                if (next == null || next.due > target) {
                    offsetNanos = target - base;
                    return ran;
                }
                queue.poll();
                // The clock reads the timer's due time while it runs.
                offsetNanos = Math.max(offsetNanos, next.due - base);
            }
            if (!next.canceled) {
                ran++;
                try {
                    next.run.run();
                } catch (RuntimeException ignored) {
                    // A failing timer task must not stop the others.
                }
            }
        }
    }

    /**
     * The timers scheduled and not yet run.
     *
     * @return their count
     */
    int pending() {
        synchronized (lock) {
            List<Task> live = new ArrayList<>();
            for (Task task : queue) {
                if (!task.canceled) {
                    live.add(task);
                }
            }
            return live.size();
        }
    }
}
