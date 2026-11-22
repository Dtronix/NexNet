package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.invocation.InvocationCounters;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.metrics.MetricTags;
import com.dtronix.nexnet.metrics.MetricsCounters;
import com.dtronix.nexnet.pipes.PipeCounters;
import com.dtronix.nexnet.pipes.PipeManager;
import com.dtronix.nexnet.pool.PoolCounters;
import com.dtronix.nexnet.reconnect.ReconnectCounters;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionCounters;
import java.util.Collections;
import java.util.List;
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
        int pipes = PipeCounters.openPipes();
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
     * A snapshot of the client's current session (C# {@code NexusClient.GetSnapshot()}). Every value the C# client
     * reports is set; only those C# leaves null are null ({@code endpoint} on a client, and a wait or time that has
     * not started).
     *
     * @param client the client
     * @return the snapshot, or null when the client has no session
     */
    public static @Nullable NexusSessionSnapshot snapshot(NexusClient<?> client) {
        NexusSession session = client.session();
        if (session == null) {
            return null;
        }
        long now = session.nanoTime();
        InvocationRuntime runtime = session.messageHandler() instanceof InvocationRuntime
            ? (InvocationRuntime) session.messageHandler() : null;
        PipeManager pipes = runtime == null ? null : runtime.pipes();
        List<NexusPipeSnapshot> pipeSnapshots = pipes == null
            ? Collections.<NexusPipeSnapshot>emptyList() : pipes.snapshots(now);
        List<NexusLargeMessageSnapshot> largeMessages = runtime == null
            ? Collections.<NexusLargeMessageSnapshot>emptyList() : runtime.largeMessageSnapshots(now);
        return new NexusSessionSnapshot(
            session.id(),
            false,
            MetricTags.transportTag(client.config()),
            null,
            client.isSecure(),
            session.pingIntervalMillis() > 0 ? NexusTimeSpan.ofMillis(session.pingIntervalMillis()) : null,
            client.state(),
            session.isEstablished(),
            since(now, session.connectedNanos()),
            since(now, session.lastReceivedNanos()),
            session.hasSent() ? since(now, session.lastSentNanos()) : null,
            session.isFlushing() ? since(now, session.flushStartedNanos()) : null,
            runtime == null ? null : runtime.budgetSnapshot(),
            runtime == null ? null : runtime.snapshot(now),
            pipeSnapshots,
            largeMessages,
            session.framesSent(),
            session.bytesSent(),
            session.framesReceived(),
            session.bytesReceived());
    }

    /**
     * How many outbound invocation ids the client's current session holds: result calls awaiting their result, calls
     * canceled after send whose late result has not arrived yet, and streamed IgnoreReturn arguments still being sent.
     * Unlike the snapshot's {@code awaitingResults}, a call canceled after send counts until its reply releases the
     * id, so this reaches 0 only when every id was released.
     *
     * @param client the client
     * @return the count, 0 when the client has no session
     */
    public static int reservedInvocationIds(NexusClient<?> client) {
        NexusSession session = client.session();
        if (session == null || !(session.messageHandler() instanceof InvocationRuntime)) {
            return 0;
        }
        return ((InvocationRuntime) session.messageHandler()).reservedIdCount();
    }

    /** The time between two session clock readings, in whole milliseconds as C# reports it. */
    private static NexusTimeSpan since(long nowNanos, long thenNanos) {
        return NexusTimeSpan.ofMillis(Math.max(0, nowNanos - thenNanos) / 1_000_000);
    }
}
