package com.dtronix.nexnet.pipes;

import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Process-wide diagnostic counters of the pipe runtime, for leak audits (H05): each returns to 0 once every pipe has
 * been freed. Read-only for users.
 */
public final class PipeCounters {
    static final AtomicInteger OPEN_PIPES = new AtomicInteger();
    static final AtomicLong BUFFERED_BYTES = new AtomicLong();

    private PipeCounters() {
    }

    /**
     * Pipes holding a receive window: created or registered and not yet freed (both sides closed and the
     * application released the pipe, or the session ended).
     *
     * @return the count
     */
    public static int openPipes() {
        return OPEN_PIPES.get();
    }

    /**
     * Bytes received on pipes and not yet read.
     *
     * @return bytes
     */
    public static long bufferedBytes() {
        return BUFFERED_BYTES.get();
    }
}
