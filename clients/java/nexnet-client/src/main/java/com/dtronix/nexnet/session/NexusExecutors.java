package com.dtronix.nexnet.session;

import java.util.concurrent.Executor;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The runtime's shared executors. The shared pool is a cached pool of daemon threads ({@code nexnet-pool-N}) that
 * grows on demand and retires idle threads after 60 seconds. It is the default callback and invocation executor, and
 * it runs every connection attempt, so no IO ever happens on the thread that calls {@code connect()}.
 */
public final class NexusExecutors {
    private static final ThreadPoolExecutor SHARED = new ThreadPoolExecutor(0, Integer.MAX_VALUE, 60,
        TimeUnit.SECONDS, new SynchronousQueue<>(), daemonThreadFactory("nexnet-pool"));

    private NexusExecutors() {
    }

    /**
     * The shared daemon pool.
     *
     * @return the executor
     */
    public static Executor shared() {
        return SHARED;
    }

    /**
     * Runs a task on an executor, or on the shared pool if the executor rejects it (for example after it was shut
     * down), so a completion is never lost.
     *
     * @param executor the executor
     * @param task the task
     */
    public static void execute(Executor executor, Runnable task) {
        try {
            executor.execute(task);
        } catch (RejectedExecutionException e) {
            if (executor == SHARED) {
                throw e;
            }
            SHARED.execute(task);
        }
    }

    /**
     * A thread factory for daemon threads named {@code <prefix>-<n>}.
     *
     * @param prefix the name prefix
     * @return the factory
     */
    public static ThreadFactory daemonThreadFactory(String prefix) {
        AtomicInteger counter = new AtomicInteger();
        return runnable -> {
            Thread thread = new Thread(runnable, prefix + "-" + counter.incrementAndGet());
            thread.setDaemon(true);
            return thread;
        };
    }
}
