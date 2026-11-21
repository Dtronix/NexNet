package com.dtronix.nexnet.session;

import java.io.FilterOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.util.List;
import java.util.concurrent.AbstractExecutorService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.LockSupport;

/**
 * Test-only scheduling jitter, off by default (K44). Set the environment variable {@code NEXNET_TEST_JITTER} (or the
 * system property {@code nexnet.test.jitter}) to a maximum delay in milliseconds, for example {@code 2}, and the test
 * transports pause for a random time before each write and before and after each flush of the session writer, and
 * the test invocation executors pause before each task. Tests that assume an order the runtime does not promise then
 * fail far more often than on an idle machine:
 * <pre>NEXNET_TEST_JITTER=2 clients/java/gradlew -p clients/java :nexnet-client:test --rerun-tasks</pre>
 */
public final class Jitter {
    /** The maximum pause in nanoseconds; 0 when jitter is off. */
    private static final long MAX_NANOS = maxNanos();

    private Jitter() {
    }

    private static long maxNanos() {
        String value = System.getProperty("nexnet.test.jitter");
        if (value == null || value.isEmpty()) {
            value = System.getenv("NEXNET_TEST_JITTER");
        }
        if (value == null || value.isEmpty()) {
            return 0;
        }
        try {
            return Math.max(0, TimeUnit.MILLISECONDS.toNanos(Long.parseLong(value.trim())));
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException("NEXNET_TEST_JITTER must be a number of milliseconds: " + value, e);
        }
    }

    /** Whether jitter is on. */
    public static boolean enabled() {
        return MAX_NANOS > 0;
    }

    /** Pauses for a random time up to the maximum, or yields; does nothing when jitter is off. */
    public static void pause() {
        if (MAX_NANOS == 0) {
            return;
        }
        ThreadLocalRandom random = ThreadLocalRandom.current();
        // A third of the pauses only yield, so short and long gaps both occur.
        if (random.nextInt(3) == 0) {
            Thread.yield();
        } else {
            LockSupport.parkNanos(random.nextLong(MAX_NANOS));
        }
    }

    /** A stream that pauses before each write and before and after each flush. */
    public static OutputStream output(OutputStream out) {
        if (MAX_NANOS == 0) {
            return out;
        }
        return new FilterOutputStream(out) {
            @Override
            public void write(byte[] b, int off, int len) throws IOException {
                pause();
                out.write(b, off, len);
            }

            @Override
            public void flush() throws IOException {
                pause();
                out.flush();
                pause();
            }
        };
    }

    /** An executor that pauses before each task (when jitter is on) and can wait until no task is pending. */
    public static Tracked executor(ExecutorService delegate) {
        return new Tracked(delegate);
    }

    /** An executor service that counts its pending tasks and pauses before each one when jitter is on. */
    public static final class Tracked extends AbstractExecutorService {
        private final ExecutorService delegate;
        private final Object lock = new Object();
        private int pending;

        Tracked(ExecutorService delegate) {
            this.delegate = delegate;
        }

        @Override
        public void execute(Runnable command) {
            synchronized (lock) {
                pending++;
            }
            try {
                delegate.execute(() -> {
                    try {
                        pause();
                        command.run();
                    } finally {
                        done();
                    }
                });
            } catch (RuntimeException e) {
                done();
                throw e;
            }
        }

        private void done() {
            synchronized (lock) {
                pending--;
                lock.notifyAll();
            }
        }

        /**
         * Waits until every task submitted so far, and every task those submitted before they ended, has finished.
         *
         * @return false on timeout
         */
        public boolean awaitIdle(long timeout, TimeUnit unit) throws InterruptedException {
            long deadline = System.nanoTime() + unit.toNanos(timeout);
            synchronized (lock) {
                while (pending > 0) {
                    long left = deadline - System.nanoTime();
                    if (left <= 0) {
                        return false;
                    }
                    TimeUnit.NANOSECONDS.timedWait(lock, left);
                }
                return true;
            }
        }

        @Override
        public void shutdown() {
            delegate.shutdown();
        }

        @Override
        public List<Runnable> shutdownNow() {
            return delegate.shutdownNow();
        }

        @Override
        public boolean isShutdown() {
            return delegate.isShutdown();
        }

        @Override
        public boolean isTerminated() {
            return delegate.isTerminated();
        }

        @Override
        public boolean awaitTermination(long timeout, TimeUnit unit) throws InterruptedException {
            return delegate.awaitTermination(timeout, unit);
        }
    }
}
