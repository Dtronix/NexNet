package com.dtronix.nexnet.invocation;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;

/**
 * The invocation credits the server advertised (protocol specification section 8): one is taken for every
 * invocation sent, fire-and-forget included, and the server returns them with {@code InvocationCredit}. Callers
 * without credit wait in FIFO order; a wait can be canceled, and fire-and-forget calls wait too (they are queued,
 * never dropped). Grants run outside the lock, in grant order, on the thread that released the credit.
 */
final class CreditGate {
    /** One waiting caller. */
    static final class Waiter {
        final Runnable onGranted;
        final Runnable onClosed;

        Waiter(Runnable onGranted, Runnable onClosed) {
            this.onGranted = onGranted;
            this.onClosed = onClosed;
        }
    }

    private final Object lock = new Object();
    private final ArrayDeque<Waiter> waiters = new ArrayDeque<>();
    private int capacity = -1;
    private int available;
    private boolean closed;

    /**
     * Sets the advertised credits once the connection completes; waiters queued before are granted.
     *
     * @param credits the server's {@code invocationCredits}
     */
    void open(int credits) {
        List<Waiter> granted;
        synchronized (lock) {
            if (closed || capacity >= 0) {
                return;
            }
            capacity = credits;
            available = credits;
            granted = drainLocked();
        }
        run(granted);
    }

    /**
     * Takes one credit for the waiter, now or when one is returned. When the gate is closed the waiter's
     * {@code onClosed} runs at once.
     *
     * @param waiter the waiter
     */
    void acquire(Waiter waiter) {
        boolean grant = false;
        boolean fail = false;
        synchronized (lock) {
            if (closed) {
                fail = true;
            } else if (capacity >= 0 && available > 0 && waiters.isEmpty()) {
                available--;
                grant = true;
            } else {
                waiters.add(waiter);
            }
        }
        if (fail) {
            waiter.onClosed.run();
        } else if (grant) {
            waiter.onGranted.run();
        }
    }

    /**
     * Removes a waiter that has not been granted.
     *
     * @param waiter the waiter
     * @return true when it was still waiting; false when it was granted (or failed) already
     */
    boolean cancel(Waiter waiter) {
        synchronized (lock) {
            return waiters.remove(waiter);
        }
    }

    /**
     * Returns credits: from the server ({@code InvocationCredit}), or a local credit taken by a call that was not
     * sent.
     *
     * @param credits the credits, at least 1
     * @return false when that is more than was taken, which the caller treats as a flow-control violation
     */
    boolean release(int credits) {
        List<Waiter> granted;
        synchronized (lock) {
            if (closed) {
                return true;
            }
            if (credits < 1 || capacity < 0 || (long) available + credits > capacity) {
                return false;
            }
            available += credits;
            granted = drainLocked();
        }
        run(granted);
        return true;
    }

    /** Closes the gate: every waiter's {@code onClosed} runs, and later acquires fail at once. */
    void close() {
        List<Waiter> failed;
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            failed = new ArrayList<>(waiters);
            waiters.clear();
        }
        for (Waiter waiter : failed) {
            waiter.onClosed.run();
        }
    }

    /** The credits not in use. */
    int available() {
        synchronized (lock) {
            return available;
        }
    }

    /** The callers waiting for credit. */
    int waiting() {
        synchronized (lock) {
            return waiters.size();
        }
    }

    private List<Waiter> drainLocked() {
        List<Waiter> granted = new ArrayList<>();
        while (available > 0 && !waiters.isEmpty()) {
            available--;
            granted.add(waiters.poll());
        }
        return granted;
    }

    private static void run(List<Waiter> granted) {
        for (Waiter waiter : granted) {
            waiter.onGranted.run();
        }
    }
}
