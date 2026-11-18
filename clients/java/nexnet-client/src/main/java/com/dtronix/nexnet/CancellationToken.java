package com.dtronix.nexnet;

import java.util.concurrent.CancellationException;

/**
 * Propagates a request to cancel an operation, like the C# {@code CancellationToken}. Obtain one from a
 * {@link CancellationTokenSource}; {@link #NONE} can never be canceled.
 *
 * <p>Passing a token to a generated proxy method cancels the invocation remotely. A token canceled before the
 * invocation is sent fails the call with a {@link CancellationException} and nothing is sent (D17).
 */
public final class CancellationToken {
    /** A token that is never canceled. */
    public static final CancellationToken NONE = new CancellationToken(null);

    private final CancellationTokenSource source;

    CancellationToken(CancellationTokenSource source) {
        this.source = source;
    }

    /**
     * Whether cancellation has been requested.
     *
     * @return true once the source has been canceled
     */
    public boolean isCancellationRequested() {
        return source != null && source.isCancellationRequested();
    }

    /**
     * Whether this token can ever be canceled.
     *
     * @return false for {@link #NONE}
     */
    public boolean canBeCanceled() {
        return source != null;
    }

    /**
     * Throws when cancellation has been requested.
     *
     * @throws CancellationException when the token is canceled
     */
    public void throwIfCancellationRequested() {
        if (isCancellationRequested()) {
            throw new CancellationException("The operation was canceled.");
        }
    }

    /**
     * Registers a callback that runs once when the token is canceled. When the token is already canceled, the
     * callback runs immediately on the calling thread. Callbacks run on the thread that calls
     * {@link CancellationTokenSource#cancel()}; keep them short.
     *
     * @param callback the callback
     * @return a registration that removes the callback when closed
     */
    public Registration register(Runnable callback) {
        if (callback == null) {
            throw new NullPointerException("callback");
        }
        if (source == null) {
            return Registration.NONE;
        }
        return source.register(callback);
    }

    @Override
    public String toString() {
        return "CancellationToken[canceled=" + isCancellationRequested() + "]";
    }
}
