package com.dtronix.nexnet.reconnect;

/**
 * Reconnects after 0, 2, 10 and 30 seconds, then keeps retrying every 30 seconds, like the C#
 * {@code DefaultReconnectionPolicy}.
 */
public final class DefaultReconnectionPolicy implements ReconnectionPolicy {
    private static final long[] DEFAULT_DELAYS = {0, 2_000, 10_000, 30_000};

    private final long[] delays;
    private final boolean continuous;

    /** Creates the default policy: 0, 2, 10 and 30 seconds, then every 30 seconds. */
    public DefaultReconnectionPolicy() {
        this(DEFAULT_DELAYS, true);
    }

    /**
     * Creates a policy with custom delays.
     *
     * @param delaysMillis the delay before each attempt, in milliseconds
     * @param continuous true to repeat the last delay forever; false to stop after the last delay
     */
    public DefaultReconnectionPolicy(long[] delaysMillis, boolean continuous) {
        if (delaysMillis.length == 0) {
            throw new IllegalArgumentException("At least one delay is required.");
        }
        for (long delay : delaysMillis) {
            if (delay < 0) {
                throw new IllegalArgumentException("Delays cannot be negative.");
            }
        }
        this.delays = delaysMillis.clone();
        this.continuous = continuous;
    }

    @Override
    public long reconnectDelayMillis(int retryAttempt) {
        if (retryAttempt < delays.length) {
            return delays[Math.max(retryAttempt, 0)];
        }
        return continuous ? delays[delays.length - 1] : STOP;
    }
}
