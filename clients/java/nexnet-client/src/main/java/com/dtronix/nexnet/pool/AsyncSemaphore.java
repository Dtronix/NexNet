package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.Registration;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;

/**
 * A FIFO counting semaphore whose waits are futures, so renting never blocks a thread. The permits bound the
 * clients a pool hands out, like the C# pool's {@code SemaphoreSlim}.
 */
final class AsyncSemaphore {
    private final Object lock = new Object();
    private final ArrayDeque<CompletableFuture<Void>> waiters = new ArrayDeque<>();
    private int permits;
    private boolean closed;

    AsyncSemaphore(int permits) {
        this.permits = permits;
    }

    /** Acquires a permit; the future fails with a CancellationException when canceled or IllegalStateException when closed. */
    CompletableFuture<Void> acquire(CancellationToken token) {
        CompletableFuture<Void> waiter;
        synchronized (lock) {
            if (closed) {
                waiter = new CompletableFuture<>();
                waiter.completeExceptionally(new IllegalStateException("The pool is closed."));
                return waiter;
            }
            if (permits > 0 && waiters.isEmpty()) {
                permits--;
                return CompletableFuture.completedFuture(null);
            }
            if (token.isCancellationRequested()) {
                waiter = new CompletableFuture<>();
                waiter.completeExceptionally(new CancellationException("The rent was canceled."));
                return waiter;
            }
            waiter = new CompletableFuture<>();
            waiters.add(waiter);
            PoolCounters.PENDING_RENTS.incrementAndGet();
        }
        Registration registration = token.register(() -> {
            boolean removed;
            synchronized (lock) {
                removed = waiters.remove(waiter);
            }
            if (removed) {
                PoolCounters.PENDING_RENTS.decrementAndGet();
                waiter.completeExceptionally(new CancellationException("The rent was canceled."));
            }
        });
        waiter.whenComplete((v, e) -> registration.close());
        return waiter;
    }

    /** Returns a permit, handing it to the oldest waiter. */
    void release() {
        CompletableFuture<Void> next;
        synchronized (lock) {
            next = waiters.poll();
            if (next == null) {
                permits++;
                return;
            }
        }
        PoolCounters.PENDING_RENTS.decrementAndGet();
        if (!next.complete(null)) {
            // Canceled concurrently: pass the permit on.
            release();
        }
    }

    /** Fails every waiter; later acquires fail. */
    void close() {
        List<CompletableFuture<Void>> pending;
        synchronized (lock) {
            closed = true;
            pending = new ArrayList<>(waiters);
            waiters.clear();
        }
        for (CompletableFuture<Void> waiter : pending) {
            PoolCounters.PENDING_RENTS.decrementAndGet();
            waiter.completeExceptionally(new IllegalStateException("The pool is closed."));
        }
    }

    int available() {
        synchronized (lock) {
            return permits;
        }
    }
}
