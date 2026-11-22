package com.dtronix.nexnet.invocation;

/**
 * The receive memory of one session (protocol specification section 9, {@code SessionBudget}): large arguments and
 * results being received, the invocation queue and, once the pipe runtime attaches, pipe windows are reserved from
 * it. Thread-safe.
 */
public final class SessionBudget {
    private final long limit;
    private long reserved;

    /**
     * Creates a budget.
     *
     * @param limit the bytes available
     */
    public SessionBudget(long limit) {
        if (limit < 0) {
            throw new IllegalArgumentException("limit");
        }
        this.limit = limit;
    }

    /**
     * Reserves bytes if they fit.
     *
     * @param bytes the bytes
     * @return true when reserved
     */
    public synchronized boolean tryReserve(long bytes) {
        if (bytes < 0) {
            throw new IllegalArgumentException("bytes");
        }
        if (reserved + bytes > limit) {
            return false;
        }
        reserved += bytes;
        return true;
    }

    /**
     * Releases a reservation.
     *
     * @param bytes the bytes reserved earlier
     */
    public synchronized void release(long bytes) {
        reserved = Math.max(0, reserved - bytes);
    }

    /**
     * The bytes reserved now.
     *
     * @return bytes
     */
    public synchronized long reserved() {
        return reserved;
    }

    /**
     * The limit.
     *
     * @return bytes
     */
    public long limit() {
        return limit;
    }
}
