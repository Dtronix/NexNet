package com.dtronix.nexnet.session;

import com.dtronix.nexnet.Registration;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** {@link SessionTimers#system()}: {@link System#nanoTime()} and one shared daemon scheduler thread. */
final class SystemSessionTimers implements SessionTimers {
    static final SystemSessionTimers INSTANCE = new SystemSessionTimers();

    private final ScheduledThreadPoolExecutor scheduler;

    private SystemSessionTimers() {
        scheduler = new ScheduledThreadPoolExecutor(1, NexusExecutors.daemonThreadFactory("nexnet-timer"));
        scheduler.setRemoveOnCancelPolicy(true);
        scheduler.setExecuteExistingDelayedTasksAfterShutdownPolicy(false);
    }

    @Override
    public long nanoTime() {
        return System.nanoTime();
    }

    @Override
    public Registration schedule(Runnable task, long delayMillis) {
        ScheduledFuture<?> future = scheduler.schedule(task, Math.max(0, delayMillis), TimeUnit.MILLISECONDS);
        return () -> future.cancel(false);
    }
}
