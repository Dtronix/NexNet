package com.dtronix.nexnet.session;

import com.dtronix.nexnet.Registration;

/**
 * The session's clock and timer scheduler. The default ({@link #system()}) uses {@link System#nanoTime()} and a
 * shared daemon {@code ScheduledExecutorService}; tests inject a manual clock to drive the handshake timeout, the
 * idle timeout and the ping interval deterministically.
 *
 * <p>Timer tasks must be short and must not block: they run on the scheduler's thread.
 */
public interface SessionTimers {
    /**
     * The monotonic clock.
     *
     * @return nanoseconds, comparable only to other values of this clock
     */
    long nanoTime();

    /**
     * Runs a task once after a delay.
     *
     * @param task the task
     * @param delayMillis the delay; 0 or less runs it as soon as possible
     * @return a registration that cancels the task when closed
     */
    Registration schedule(Runnable task, long delayMillis);

    /**
     * The system clock and the shared timer thread ({@code nexnet-timer}, a daemon).
     *
     * @return the timers
     */
    static SessionTimers system() {
        return SystemSessionTimers.INSTANCE;
    }
}
