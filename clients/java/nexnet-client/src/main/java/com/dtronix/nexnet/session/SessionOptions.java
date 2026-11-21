package com.dtronix.nexnet.session;

/**
 * The session limits and timings that are not part of {@code ClientConfig}. Immutable; start from
 * {@link #defaults()}. The defaults are the C# untrusted defaults (protocol specification section 9).
 */
public final class SessionOptions {
    /** The smallest {@code maxMessageSize} a side may advertise: one frame. */
    public static final int MIN_MAX_MESSAGE_SIZE = 65_526;
    /** The default {@link #maxMessageSize()}: 4 MiB. */
    public static final int DEFAULT_MAX_MESSAGE_SIZE = 4 * 1024 * 1024;
    /** The default {@link #invocationCredits()}: 32. */
    public static final int DEFAULT_INVOCATION_CREDITS = 32;
    /** The default {@link #disconnectDelayMillis()}: 200 ms. */
    public static final int DEFAULT_DISCONNECT_DELAY_MILLIS = 200;
    /** The default {@link #writeQueueCapacity()}: 1024 frames. */
    public static final int DEFAULT_WRITE_QUEUE_CAPACITY = 1024;
    /** The default {@link #threadJoinTimeoutMillis()}: 5 seconds. */
    public static final int DEFAULT_THREAD_JOIN_TIMEOUT_MILLIS = 5_000;

    private static final SessionOptions DEFAULTS = new SessionOptions(DEFAULT_MAX_MESSAGE_SIZE,
        DEFAULT_INVOCATION_CREDITS, DEFAULT_DISCONNECT_DELAY_MILLIS, DEFAULT_WRITE_QUEUE_CAPACITY,
        DEFAULT_THREAD_JOIN_TIMEOUT_MILLIS);

    private final int maxMessageSize;
    private final int invocationCredits;
    private final int disconnectDelayMillis;
    private final int writeQueueCapacity;
    private final int threadJoinTimeoutMillis;

    private SessionOptions(int maxMessageSize, int invocationCredits, int disconnectDelayMillis,
                           int writeQueueCapacity, int threadJoinTimeoutMillis) {
        if (maxMessageSize < MIN_MAX_MESSAGE_SIZE) {
            throw new IllegalArgumentException("maxMessageSize must be at least " + MIN_MAX_MESSAGE_SIZE);
        }
        if (invocationCredits < 1 || invocationCredits > 0xFFFF) {
            throw new IllegalArgumentException("invocationCredits must be 1 to 65535");
        }
        if (disconnectDelayMillis < 0 || disconnectDelayMillis > 10_000) {
            throw new IllegalArgumentException("disconnectDelayMillis must be 0 to 10000");
        }
        if (writeQueueCapacity < 1) {
            throw new IllegalArgumentException("writeQueueCapacity must be positive");
        }
        if (threadJoinTimeoutMillis < 0) {
            throw new IllegalArgumentException("threadJoinTimeoutMillis must not be negative");
        }
        this.maxMessageSize = maxMessageSize;
        this.invocationCredits = invocationCredits;
        this.disconnectDelayMillis = disconnectDelayMillis;
        this.writeQueueCapacity = writeQueueCapacity;
        this.threadJoinTimeoutMillis = threadJoinTimeoutMillis;
    }

    /**
     * The default options.
     *
     * @return the defaults
     */
    public static SessionOptions defaults() {
        return DEFAULTS;
    }

    /**
     * The largest argument, result or channel item this client accepts, advertised in the greeting.
     *
     * @return bytes
     */
    public int maxMessageSize() {
        return maxMessageSize;
    }

    /**
     * How many invocations the server may have queued at this client, advertised in the greeting.
     *
     * @return the credits
     */
    public int invocationCredits() {
        return invocationCredits;
    }

    /**
     * How long the session waits after writing a disconnect byte before closing the transport, so the byte reaches
     * the wire. 0 closes at once.
     *
     * @return milliseconds
     */
    public int disconnectDelayMillis() {
        return disconnectDelayMillis;
    }

    /**
     * How many frames may wait for the writer thread before a sender blocks.
     *
     * @return the capacity
     */
    public int writeQueueCapacity() {
        return writeQueueCapacity;
    }

    /**
     * How long closing waits for the reader and writer threads to end.
     *
     * @return milliseconds
     */
    public int threadJoinTimeoutMillis() {
        return threadJoinTimeoutMillis;
    }

    /**
     * A copy with another advertised {@link #maxMessageSize()}.
     *
     * @param value bytes, at least {@link #MIN_MAX_MESSAGE_SIZE}
     * @return the options
     */
    public SessionOptions withMaxMessageSize(int value) {
        return new SessionOptions(value, invocationCredits, disconnectDelayMillis, writeQueueCapacity,
            threadJoinTimeoutMillis);
    }

    /**
     * A copy with other advertised {@link #invocationCredits()}.
     *
     * @param value 1 to 65535
     * @return the options
     */
    public SessionOptions withInvocationCredits(int value) {
        return new SessionOptions(maxMessageSize, value, disconnectDelayMillis, writeQueueCapacity,
            threadJoinTimeoutMillis);
    }

    /**
     * A copy with another {@link #disconnectDelayMillis()}.
     *
     * @param value 0 to 10000 milliseconds
     * @return the options
     */
    public SessionOptions withDisconnectDelayMillis(int value) {
        return new SessionOptions(maxMessageSize, invocationCredits, value, writeQueueCapacity,
            threadJoinTimeoutMillis);
    }

    /**
     * A copy with another {@link #writeQueueCapacity()}.
     *
     * @param value the capacity, positive
     * @return the options
     */
    public SessionOptions withWriteQueueCapacity(int value) {
        return new SessionOptions(maxMessageSize, invocationCredits, disconnectDelayMillis, value,
            threadJoinTimeoutMillis);
    }

    /**
     * A copy with another {@link #threadJoinTimeoutMillis()}.
     *
     * @param value milliseconds
     * @return the options
     */
    public SessionOptions withThreadJoinTimeoutMillis(int value) {
        return new SessionOptions(maxMessageSize, invocationCredits, disconnectDelayMillis, writeQueueCapacity,
            value);
    }
}
