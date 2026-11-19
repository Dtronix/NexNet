package com.dtronix.nexnet.session;

import org.jspecify.annotations.Nullable;

/**
 * Reports a lack of progress (protocol specification section 9): a large message, pipe or channel item whose data
 * stopped arriving. The session asks every probe on each keepalive tick and, when one reports a stall, disconnects
 * with {@code Stalled}. Probes run on the timer thread: they must be quick, must not block and must be thread-safe.
 */
@FunctionalInterface
public interface ProgressProbe {
    /**
     * Describes something that made no progress for longer than the timeout.
     *
     * @param nowNanos the session clock ({@link NexusSession#nanoTime()})
     * @param timeoutNanos the progress timeout
     * @return a description for the log, or null when nothing stalled
     */
    @Nullable String findStalled(long nowNanos, long timeoutNanos);
}
