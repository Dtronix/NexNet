package com.dtronix.nexnet;

/**
 * A handle that removes a callback or listener when closed. Closing is idempotent and never throws.
 */
@FunctionalInterface
public interface Registration extends AutoCloseable {
    /** A registration that does nothing when closed. */
    Registration NONE = () -> {
    };

    /** Removes the callback or listener. Calling it again has no effect. */
    @Override
    void close();
}
