package com.dtronix.nexnet.diagnostics;

/**
 * Counters of a runtime layer that registers itself with {@link Diagnostics#registerCounterSource} (for example
 * the pipe layer). Must be cheap and thread-safe.
 */
public interface CounterSource {
    /**
     * Pipes open or closing.
     *
     * @return the count
     */
    default int pipes() {
        return 0;
    }

    /**
     * Timers scheduled.
     *
     * @return the count
     */
    default int timers() {
        return 0;
    }

    /**
     * Listeners held on clients, sessions or platform objects.
     *
     * @return the count
     */
    default int subscriptions() {
        return 0;
    }
}
