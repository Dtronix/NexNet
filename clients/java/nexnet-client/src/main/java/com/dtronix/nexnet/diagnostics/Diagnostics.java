package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.invocation.InvocationCounters;
import com.dtronix.nexnet.metrics.MetricTags;
import com.dtronix.nexnet.metrics.MetricsCounters;
import com.dtronix.nexnet.pool.PoolCounters;
import com.dtronix.nexnet.reconnect.ReconnectCounters;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionCounters;
import java.util.Collections;
import java.util.concurrent.CopyOnWriteArrayList;
import org.jspecify.annotations.Nullable;

/**
 * Diagnostics: the leak-audit counters ({@link #counters()}) and session snapshots ({@link #snapshot}).
 */
public final class Diagnostics {
    private static final CopyOnWriteArrayList<CounterSource> SOURCES = new CopyOnWriteArrayList<>();

    private Diagnostics() {
    }

    /**
     * The process-wide counters of every runtime layer. Each is 0 once every client and every pool is closed; the
     * interop harness checks this after every test (H05).
     *
     * @return the counters
     */
    public static NexusCounters counters() {
        int pipes = 0;
        int timers = SessionCounters.activeTimers() + ReconnectCounters.pendingDelays() + PoolCounters.healthChecks();
        int subscriptions = MetricsCounters.subscriptions();
        for (CounterSource source : SOURCES) {
            pipes += source.pipes();
            timers += source.timers();
            subscriptions += source.subscriptions();
        }
        return new NexusCounters(
            SessionCounters.openSessions(),
            SessionCounters.liveThreads(),
            SessionCounters.pendingWrites(),
            timers,
            InvocationCounters.pendingCalls() + InvocationCounters.inboundInvocations(),
            InvocationCounters.largeMessages(),
            pipes,
            subscriptions,
            PoolCounters.pooledClients());
    }

    /**
     * Whether every counter is 0.
     *
     * @return true when nothing is open
     */
    public static boolean isIdle() {
        return counters().isIdle();
    }

    /**
     * Adds the counters of another runtime layer (for example the pipe layer) to {@link #counters()}.
     *
     * @param source the source
     * @return a registration that removes it
     */
    public static Registration registerCounterSource(CounterSource source) {
        if (source == null) {
            throw new NullPointerException("source");
        }
        SOURCES.add(source);
        return () -> SOURCES.remove(source);
    }

    /**
     * A snapshot of the client's current session (C# {@code NexusClient.GetSnapshot()}).
     *
     * <p>This runtime does not track every value yet: the transport counters, the timings (other than the ping
     * interval), the budget and the
     * invocation queues are null, and the pipe and large-message lists are empty, until the session and invocation
     * layers expose them.
     *
     * @param client the client
     * @return the snapshot, or null when the client has no session
     */
    public static @Nullable NexusSessionSnapshot snapshot(NexusClient<?> client) {
        NexusSession session = client.session();
        if (session == null) {
            return null;
        }
        return new NexusSessionSnapshot(
            session.id(),
            false,
            MetricTags.transportTag(client.config()),
            null,
            client.isSecure(),
            session.pingIntervalMillis() > 0 ? NexusTimeSpan.ofMillis(session.pingIntervalMillis()) : null,
            client.state(),
            session.isConnected(),
            null,
            null,
            null,
            null,
            null,
            null,
            Collections.emptyList(),
            Collections.emptyList(),
            null,
            null,
            null,
            null);
    }
}
