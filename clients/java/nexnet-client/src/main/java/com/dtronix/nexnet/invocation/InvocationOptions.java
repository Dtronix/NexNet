package com.dtronix.nexnet.invocation;

/**
 * The invocation limits of a client session. Immutable; start from {@link #defaults()}, which are the C# untrusted
 * defaults ({@code MaxConcurrentConnectionInvocations} 2, {@code MaxSeparatePoolInvocations} 4,
 * {@code SessionBudget} 16 MiB, {@code RemoteExceptionDetail.None}, {@code InvocationQueueWarningThreshold} 5000 ms).
 */
public final class InvocationOptions {
    /** The default {@link #maxConcurrentInvocations()}. */
    public static final int DEFAULT_MAX_CONCURRENT_INVOCATIONS = 2;
    /** The default {@link #maxSeparatePoolInvocations()}. */
    public static final int DEFAULT_MAX_SEPARATE_POOL_INVOCATIONS = 4;
    /** The default {@link #sessionBudget()}: 16 MiB. */
    public static final long DEFAULT_SESSION_BUDGET = 16L * 1024 * 1024;
    /** The default {@link #invocationQueueWarningThresholdMillis()}: 5 s. */
    public static final int DEFAULT_INVOCATION_QUEUE_WARNING_THRESHOLD_MILLIS = 5000;

    private static final InvocationOptions DEFAULTS = new InvocationOptions(DEFAULT_MAX_CONCURRENT_INVOCATIONS,
        DEFAULT_MAX_SEPARATE_POOL_INVOCATIONS, DEFAULT_SESSION_BUDGET, RemoteExceptionDetail.NONE,
        DEFAULT_INVOCATION_QUEUE_WARNING_THRESHOLD_MILLIS);

    private final int maxConcurrentInvocations;
    private final int maxSeparatePoolInvocations;
    private final long sessionBudget;
    private final RemoteExceptionDetail remoteExceptionDetail;
    private final int invocationQueueWarningThresholdMillis;

    private InvocationOptions(int maxConcurrentInvocations, int maxSeparatePoolInvocations, long sessionBudget,
                              RemoteExceptionDetail remoteExceptionDetail, int invocationQueueWarningThresholdMillis) {
        if (maxConcurrentInvocations < 1 || maxConcurrentInvocations > 1000) {
            throw new IllegalArgumentException("maxConcurrentInvocations must be 1 to 1000");
        }
        if (maxSeparatePoolInvocations < 1 || maxSeparatePoolInvocations > 1000) {
            throw new IllegalArgumentException("maxSeparatePoolInvocations must be 1 to 1000");
        }
        if (sessionBudget < 64 * 1024) {
            throw new IllegalArgumentException("sessionBudget must be at least 64 KiB");
        }
        if (remoteExceptionDetail == null) {
            throw new NullPointerException("remoteExceptionDetail");
        }
        if (invocationQueueWarningThresholdMillis < 0 || invocationQueueWarningThresholdMillis > 600_000) {
            throw new IllegalArgumentException("invocationQueueWarningThresholdMillis must be 0 to 600000");
        }
        this.maxConcurrentInvocations = maxConcurrentInvocations;
        this.maxSeparatePoolInvocations = maxSeparatePoolInvocations;
        this.sessionBudget = sessionBudget;
        this.remoteExceptionDetail = remoteExceptionDetail;
        this.invocationQueueWarningThresholdMillis = invocationQueueWarningThresholdMillis;
    }

    /**
     * The default options.
     *
     * @return the defaults
     */
    public static InvocationOptions defaults() {
        return DEFAULTS;
    }

    /**
     * How many client nexus invocations run at once; further ones queue until a slot frees.
     *
     * @return the count
     */
    public int maxConcurrentInvocations() {
        return maxConcurrentInvocations;
    }

    /**
     * How many invocations of separate-pool methods ({@code [NexusMethod(SeparatePool = true)]}) run at once, apart
     * from {@link #maxConcurrentInvocations()}.
     *
     * @return the count
     */
    public int maxSeparatePoolInvocations() {
        return maxSeparatePoolInvocations;
    }

    /**
     * The receive memory of the session, in bytes; the invocation queue ({@code invocationCredits} x 64 KiB) is
     * reserved from it, and large arguments and results that do not fit the rest are refused with {@code Busy}.
     *
     * @return bytes
     */
    public long sessionBudget() {
        return sessionBudget;
    }

    /**
     * How much of a client nexus method's exception is sent to the server.
     *
     * @return the detail
     */
    public RemoteExceptionDetail remoteExceptionDetail() {
        return remoteExceptionDetail;
    }

    /**
     * How long, in milliseconds, an invocation from the server may wait for an execution slot before the client logs
     * a warning naming the method, the wait and the pool limit (C# {@code InvocationQueueWarningThreshold}). The
     * warning is logged at most once every 30 seconds per session, with the number of slow invocations since the last
     * one. 0 disables it.
     *
     * @return milliseconds, 0 to 600000
     */
    public int invocationQueueWarningThresholdMillis() {
        return invocationQueueWarningThresholdMillis;
    }

    /**
     * A copy with another {@link #maxConcurrentInvocations()}.
     *
     * @param value 1 to 1000
     * @return the options
     */
    public InvocationOptions withMaxConcurrentInvocations(int value) {
        return new InvocationOptions(value, maxSeparatePoolInvocations, sessionBudget, remoteExceptionDetail,
            invocationQueueWarningThresholdMillis);
    }

    /**
     * A copy with another {@link #maxSeparatePoolInvocations()}.
     *
     * @param value 1 to 1000
     * @return the options
     */
    public InvocationOptions withMaxSeparatePoolInvocations(int value) {
        return new InvocationOptions(maxConcurrentInvocations, value, sessionBudget, remoteExceptionDetail,
            invocationQueueWarningThresholdMillis);
    }

    /**
     * A copy with another {@link #sessionBudget()}.
     *
     * @param value bytes, at least 64 KiB
     * @return the options
     */
    public InvocationOptions withSessionBudget(long value) {
        return new InvocationOptions(maxConcurrentInvocations, maxSeparatePoolInvocations, value,
            remoteExceptionDetail, invocationQueueWarningThresholdMillis);
    }

    /**
     * A copy with another {@link #remoteExceptionDetail()}.
     *
     * @param value the detail
     * @return the options
     */
    public InvocationOptions withRemoteExceptionDetail(RemoteExceptionDetail value) {
        return new InvocationOptions(maxConcurrentInvocations, maxSeparatePoolInvocations, sessionBudget, value,
            invocationQueueWarningThresholdMillis);
    }

    /**
     * A copy with another {@link #invocationQueueWarningThresholdMillis()}.
     *
     * @param value milliseconds, 0 (no warning) to 600000
     * @return the options
     */
    public InvocationOptions withInvocationQueueWarningThresholdMillis(int value) {
        return new InvocationOptions(maxConcurrentInvocations, maxSeparatePoolInvocations, sessionBudget,
            remoteExceptionDetail, value);
    }
}
