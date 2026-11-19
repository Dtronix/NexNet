package com.dtronix.nexnet.session;

import java.util.ArrayDeque;
import java.util.concurrent.Executor;

/**
 * Runs tasks one at a time, in submission order, on another executor. State listeners use it so they observe
 * changes in order even on a multi-threaded callback executor.
 */
public final class SerialExecutor implements Executor {
    private final Executor target;
    private final ArrayDeque<Runnable> tasks = new ArrayDeque<>();
    private boolean running;

    /**
     * Creates the executor.
     *
     * @param target the executor the tasks run on
     */
    public SerialExecutor(Executor target) {
        if (target == null) {
            throw new NullPointerException("target");
        }
        this.target = target;
    }

    @Override
    public void execute(Runnable task) {
        synchronized (tasks) {
            tasks.add(task);
            if (running) {
                return;
            }
            running = true;
        }
        NexusExecutors.execute(target, this::drain);
    }

    private void drain() {
        while (true) {
            Runnable next;
            synchronized (tasks) {
                next = tasks.poll();
                if (next == null) {
                    running = false;
                    return;
                }
            }
            try {
                next.run();
            } catch (RuntimeException e) {
                // A failing task must not stop the ones after it.
                Thread current = Thread.currentThread();
                Thread.UncaughtExceptionHandler handler = current.getUncaughtExceptionHandler();
                if (handler != null) {
                    handler.uncaughtException(current, e);
                }
            }
        }
    }
}
