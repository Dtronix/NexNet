package com.dtronix.nexnet.reconnect;

import com.dtronix.nexnet.DisconnectReason;
import java.util.Arrays;
import java.util.Collections;
import java.util.EnumSet;
import java.util.Set;

/**
 * Reconnects after 0, 2, 10 and 30 seconds, then keeps retrying every 30 seconds, like the C#
 * {@code DefaultReconnectionPolicy}. By default it reconnects for {@link DisconnectReason#SOCKET_ERROR},
 * {@link DisconnectReason#TIMEOUT} and {@link DisconnectReason#SERVER_RESTARTING}.
 */
public final class DefaultReconnectionPolicy implements ReconnectionPolicy {
    private static final long[] DEFAULT_DELAYS = {0, 2_000, 10_000, 30_000};

    private final long[] delays;
    private final boolean continuous;
    private final Set<DisconnectReason> reasons;

    /** Creates the default policy: 0, 2, 10 and 30 seconds, then every 30 seconds. */
    public DefaultReconnectionPolicy() {
        this(DEFAULT_DELAYS, true);
    }

    /**
     * Creates a policy with custom delays and the default reasons.
     *
     * @param delaysMillis the delay before each attempt, in milliseconds
     * @param continuous true to repeat the last delay forever; false to stop after the last delay
     */
    public DefaultReconnectionPolicy(long[] delaysMillis, boolean continuous) {
        this(delaysMillis, continuous, EnumSet.of(DisconnectReason.SOCKET_ERROR, DisconnectReason.TIMEOUT,
            DisconnectReason.SERVER_RESTARTING));
    }

    /**
     * Creates a policy with custom delays and reasons. Add {@link DisconnectReason#SERVER_SHUTDOWN} to reconnect
     * after the server stops and starts again; {@code GRACEFUL} and {@code AUTHENTICATION} are best left out.
     *
     * @param delaysMillis the delay before each attempt, in milliseconds
     * @param continuous true to repeat the last delay forever; false to stop after the last delay
     * @param reconnectReasons the disconnect reasons to reconnect for
     */
    public DefaultReconnectionPolicy(long[] delaysMillis, boolean continuous, Set<DisconnectReason> reconnectReasons) {
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
        this.reasons = reconnectReasons.isEmpty() ? Collections.<DisconnectReason>emptySet()
            : Collections.unmodifiableSet(EnumSet.copyOf(reconnectReasons));
    }

    @Override
    public long reconnectDelayMillis(int retryAttempt) {
        if (retryAttempt < delays.length) {
            return delays[Math.max(retryAttempt, 0)];
        }
        return continuous ? delays[delays.length - 1] : STOP;
    }

    @Override
    public boolean shouldReconnect(DisconnectReason reason) {
        return reasons.contains(reason);
    }

    /**
     * The delays, in milliseconds.
     *
     * @return a copy of the delays
     */
    public long[] delaysMillis() {
        return delays.clone();
    }

    /**
     * Whether the last delay repeats forever.
     *
     * @return true when continuous
     */
    public boolean continuous() {
        return continuous;
    }

    /**
     * The disconnect reasons this policy reconnects for.
     *
     * @return the reasons (unmodifiable)
     */
    public Set<DisconnectReason> reconnectReasons() {
        return reasons;
    }

    @Override
    public String toString() {
        return "DefaultReconnectionPolicy[" + Arrays.toString(delays) + (continuous ? ", continuous" : "") + ", "
            + reasons + "]";
    }
}
