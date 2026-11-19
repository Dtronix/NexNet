package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * One call to the server that expects a result, and its user-visible future. {@link #cancel(boolean)} cancels the
 * call: before it is sent nothing is sent; after, {@code InvocationCancellation} is sent and the late result is
 * consumed (protocol specification section 4.5).
 *
 * @param <R> the result type
 */
final class OutboundCall<R> extends CompletableFuture<R> {
    /** Waiting for invocation credit. */
    static final int WAITING = 0;
    /** Holding an invocation id; the invocation is being queued or streamed. */
    static final int SENDING = 1;
    /** Queued for the writer in full. */
    static final int SENT = 2;

    final InvocationRuntime runtime;
    final int methodId;
    final @Nullable Formatter<R> formatter;
    byte[] arguments = new byte[0];
    volatile CreditGate.@Nullable Waiter waiter;

    // Guarded by this.
    int state = WAITING;
    int invocationId = -1;
    boolean cancellationSent;

    OutboundCall(InvocationRuntime runtime, int methodId, @Nullable Formatter<R> formatter) {
        this.runtime = runtime;
        this.methodId = methodId;
        this.formatter = formatter;
    }

    /**
     * Cancels the call. Completes this future canceled at once (on the calling thread), then tells the runtime, which
     * sends {@code InvocationCancellation} if the invocation was already sent.
     */
    @Override
    public boolean cancel(boolean mayInterruptIfRunning) {
        boolean canceled = super.cancel(mayInterruptIfRunning);
        if (canceled) {
            runtime.onCallCanceled(this);
        }
        return canceled;
    }

    /** Completes the call canceled without running the cancellation logic (the session ended, or nothing was sent). */
    void completeCanceled(String message) {
        completeCanceled(new CancellationException(message));
    }

    /** Completes the call with this cancellation, which may carry a cause (amendment C-2). */
    void completeCanceled(CancellationException canceled) {
        super.completeExceptionally(canceled);
    }

    /**
     * Records the transition to {@link #SENT}.
     *
     * @return true when the call was canceled meanwhile and the caller must send the cancellation
     */
    synchronized boolean markSent() {
        state = SENT;
        if (isCancelled() && !cancellationSent) {
            cancellationSent = true;
            return true;
        }
        return false;
    }
}
