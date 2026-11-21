package com.dtronix.nexnet;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * Signals a {@link CancellationToken} that it should be canceled. Thread-safe.
 */
public final class CancellationTokenSource implements AutoCloseable {
    private final CancellationToken token = new CancellationToken(this);
    private final Object lock = new Object();
    private Set<Runnable> callbacks = new LinkedHashSet<>();
    private volatile boolean canceled;

    /** Creates a source that is not canceled. */
    public CancellationTokenSource() {
    }

    /**
     * The token controlled by this source.
     *
     * @return the token
     */
    public CancellationToken token() {
        return token;
    }

    /**
     * Whether {@link #cancel()} has been called.
     *
     * @return true once canceled
     */
    public boolean isCancellationRequested() {
        return canceled;
    }

    /**
     * Requests cancellation and runs the registered callbacks once, in registration order, on the calling thread.
     * Later calls have no effect. A callback that throws does not stop the others; the first exception is rethrown
     * after all callbacks have run.
     */
    public void cancel() {
        List<Runnable> toRun;
        synchronized (lock) {
            if (canceled) {
                return;
            }
            canceled = true;
            toRun = new ArrayList<>(callbacks);
            callbacks = null;
        }
        RuntimeException first = null;
        for (Runnable callback : toRun) {
            try {
                callback.run();
            } catch (RuntimeException e) {
                if (first == null) {
                    first = e;
                }
            }
        }
        if (first != null) {
            throw first;
        }
    }

    /** Releases the registered callbacks without canceling. The token is no longer canceled by this source. */
    @Override
    public void close() {
        synchronized (lock) {
            if (callbacks != null) {
                callbacks.clear();
            }
        }
    }

    Registration register(Runnable callback) {
        // Wrap so the same Runnable may be registered more than once.
        Runnable entry = callback::run;
        synchronized (lock) {
            if (!canceled) {
                callbacks.add(entry);
                return () -> {
                    synchronized (lock) {
                        if (callbacks != null) {
                            callbacks.remove(entry);
                        }
                    }
                };
            }
        }
        callback.run();
        return Registration.NONE;
    }
}
