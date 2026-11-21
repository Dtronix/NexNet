package com.dtronix.nexnet.metrics;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.pipes.PipeManager;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionObservers;
import com.dtronix.nexnet.session.SessionTimers;
import java.util.ArrayList;
import java.util.Collections;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import org.jspecify.annotations.Nullable;

/**
 * Client metrics with the C# instrument names and tags ({@link NexusInstruments}). Track clients with
 * {@link #track}; listeners ({@link NexusMetricsListener}) receive the event instruments (counters and histograms) as
 * they happen, and {@link #collect} reads the observable instruments from the live sessions, as the C# observable
 * instruments do: the send and receive paths pay nothing for metrics.
 *
 * <p>A tracked client's sessions are counted from the moment their transport connects (the session starts) until
 * they end, the handshake included: {@code nexnet.session.duration} runs from the transport connection, and a session
 * that ends during the handshake is reported to {@code nexnet.session.disconnects} with
 * {@code nexnet.session.phase=handshake}. The session, invocation and pipe layers report the other event instruments
 * (pipe resets and refusals, invocation queue waits, pipe credit waits, large-message aborts) through the session's
 * metrics hook. {@code nexnet.connection.rejections} and {@code nexnet.invocation.busy} are server-only, and a client
 * never records {@code nexnet.invocation.credit_drops} (only a server's multi-session invocations drop calls for lack
 * of credit). {@code nexnet.io.frames} and {@code nexnet.io.bytes} stay monotonic: the totals of ended sessions, and
 * of clients no longer tracked, are kept.
 *
 * <p>Listeners are called synchronously where the event happens, which may be a connection's reader thread: keep them
 * cheap. A listener that throws is ignored. Thread-safe.
 */
public final class NexusMetrics implements AutoCloseable {
    private static final Object SHARED_LOCK = new Object();
    private static @Nullable NexusMetrics shared;

    private final CopyOnWriteArrayList<NexusMetricsListener> listeners = new CopyOnWriteArrayList<>();
    private final Map<NexusClient<?>, Tracked> clients = new IdentityHashMap<>();
    /** Transport totals of ended sessions, by tag set; kept after untrack so the counters stay monotonic. */
    private final Map<Map<String, String>, long[]> retired = new LinkedHashMap<>();
    private @Nullable Registration observer;

    /** One tracked client. */
    private static final class Tracked {
        final NexusClient<?> client;
        final Map<String, String> tags;
        /** The session being counted, from its start until it ends. */
        @Nullable NexusSession session;
        @Nullable Registration sink;

        Tracked(NexusClient<?> client) {
            this.client = client;
            Map<String, String> t = new LinkedHashMap<>();
            t.put(MetricTags.ROLE, "client");
            t.put(MetricTags.TRANSPORT, MetricTags.transportTag(client.config()));
            this.tags = Collections.unmodifiableMap(t);
        }
    }

    /** Creates an instance. */
    public NexusMetrics() {
    }

    /**
     * Creates an instance. Durations are measured on each session's own clock, so the clock given here is not used;
     * the constructor is kept for compatibility.
     *
     * @param clock unused
     */
    public NexusMetrics(SessionTimers clock) {
        if (clock == null) {
            throw new NullPointerException("clock");
        }
    }

    /**
     * A process-wide instance, like the C# shared meter.
     *
     * @return the instance
     */
    public static NexusMetrics shared() {
        synchronized (SHARED_LOCK) {
            if (shared == null) {
                shared = new NexusMetrics();
            }
            return shared;
        }
    }

    /**
     * Adds a listener for the event instruments.
     *
     * @param listener the listener
     * @return a registration that removes it
     */
    public Registration addListener(NexusMetricsListener listener) {
        if (listener == null) {
            throw new NullPointerException("listener");
        }
        listeners.add(listener);
        return () -> listeners.remove(listener);
    }

    /**
     * Starts reporting a client. Tracking a client twice is a no-op. A session the client already has is counted from
     * now on; its duration still runs from its transport connection.
     *
     * @param client the client
     * @return a registration that stops reporting it (same as {@link #untrack})
     */
    public Registration track(NexusClient<?> client) {
        if (client == null) {
            throw new NullPointerException("client");
        }
        Tracked tracked;
        synchronized (clients) {
            if (clients.containsKey(client)) {
                return () -> untrack(client);
            }
            tracked = new Tracked(client);
            clients.put(client, tracked);
            if (observer == null) {
                observer = SessionObservers.add(this::onSessionStarted);
                MetricsCounters.SUBSCRIPTIONS.incrementAndGet();
            }
        }
        NexusSession current = client.session();
        if (current != null) {
            attach(tracked, current);
        }
        return () -> untrack(client);
    }

    /**
     * Stops reporting a client. The transport totals of its sessions stay in {@code nexnet.io.*}.
     *
     * @param client the client
     */
    public void untrack(NexusClient<?> client) {
        Registration removeObserver = null;
        synchronized (clients) {
            Tracked tracked = clients.remove(client);
            if (tracked == null) {
                return;
            }
            retireLocked(tracked);
            if (clients.isEmpty() && observer != null) {
                removeObserver = observer;
                observer = null;
            }
        }
        if (removeObserver != null) {
            removeObserver.close();
            MetricsCounters.SUBSCRIPTIONS.decrementAndGet();
        }
    }

    /**
     * The number of clients tracked.
     *
     * @return the count
     */
    public int trackedClients() {
        synchronized (clients) {
            return clients.size();
        }
    }

    /**
     * Reads the observable instruments: one measurement per instrument and tag set (two for the instruments with a
     * direction or an invocation pool).
     *
     * @param observer receives the measurements
     */
    public void collect(MeasurementObserver observer) {
        Map<Map<String, String>, long[]> sums = new LinkedHashMap<>();
        List<Tracked> snapshot;
        synchronized (clients) {
            for (Map.Entry<Map<String, String>, long[]> entry : retired.entrySet()) {
                long[] sum = sumFor(sums, entry.getKey());
                for (int i = 0; i < 4; i++) {
                    sum[IO_FRAMES_SENT + i] += entry.getValue()[i];
                }
            }
            snapshot = new ArrayList<>(clients.values());
            for (Tracked tracked : snapshot) {
                sumFor(sums, tracked.tags);
            }
        }
        for (Tracked tracked : snapshot) {
            NexusSession session;
            synchronized (clients) {
                session = tracked.session;
            }
            if (session == null) {
                continue;
            }
            long[] sum = sums.get(tracked.tags);
            sum[IO_FRAMES_SENT] += session.framesSent();
            sum[IO_BYTES_SENT] += session.bytesSent();
            sum[IO_FRAMES_RECEIVED] += session.framesReceived();
            sum[IO_BYTES_RECEIVED] += session.bytesReceived();
            if (session.isClosing()) {
                continue;
            }
            sum[ACTIVE]++;
            if (!session.isEstablished()) {
                sum[HANDSHAKING]++;
            }
            if (session.messageHandler() instanceof InvocationRuntime) {
                InvocationRuntime runtime = (InvocationRuntime) session.messageHandler();
                sum[BUDGET_RESERVED] += runtime.budget().reserved();
                sum[BUDGET_LIMIT] += runtime.budget().limit();
                sum[QUEUED] += runtime.queued(false);
                sum[SEPARATE_QUEUED] += runtime.queued(true);
                sum[EXECUTING] += runtime.executing(false);
                sum[SEPARATE_EXECUTING] += runtime.executing(true);
                sum[CREDIT_WAITS] += runtime.creditWaits();
                sum[LARGE_INBOUND] += runtime.largeMessagesInbound();
                sum[LARGE_OUTBOUND] += runtime.largeMessagesOutbound();
                PipeManager pipes = runtime.pipes();
                if (pipes != null) {
                    int open = pipes.openPipes();
                    sum[PIPES_OPEN] += open;
                    sum[PIPE_WINDOW] += (long) open * pipes.options().pipeWindow();
                }
            }
        }
        for (Map.Entry<Map<String, String>, long[]> entry : sums.entrySet()) {
            Map<String, String> tags = entry.getKey();
            long[] sum = entry.getValue();
            Map<String, String> sent = with(tags, MetricTags.DIRECTION, "sent");
            Map<String, String> received = with(tags, MetricTags.DIRECTION, "received");
            Map<String, String> normal = with(tags, MetricTags.INVOCATION_POOL, "normal");
            Map<String, String> separate = with(tags, MetricTags.INVOCATION_POOL, "separate");
            observer.observe(NexusInstruments.SESSION_ACTIVE, sum[ACTIVE], tags);
            observer.observe(NexusInstruments.SESSION_HANDSHAKING, sum[HANDSHAKING], tags);
            observer.observe(NexusInstruments.IO_FRAMES, sum[IO_FRAMES_SENT], sent);
            observer.observe(NexusInstruments.IO_FRAMES, sum[IO_FRAMES_RECEIVED], received);
            observer.observe(NexusInstruments.IO_BYTES, sum[IO_BYTES_SENT], sent);
            observer.observe(NexusInstruments.IO_BYTES, sum[IO_BYTES_RECEIVED], received);
            observer.observe(NexusInstruments.BUDGET_RESERVED, sum[BUDGET_RESERVED], tags);
            observer.observe(NexusInstruments.BUDGET_LIMIT, sum[BUDGET_LIMIT], tags);
            observer.observe(NexusInstruments.PIPE_OPEN, sum[PIPES_OPEN], tags);
            observer.observe(NexusInstruments.PIPE_WINDOW, sum[PIPE_WINDOW], tags);
            observer.observe(NexusInstruments.INVOCATION_QUEUED, sum[QUEUED], normal);
            observer.observe(NexusInstruments.INVOCATION_QUEUED, sum[SEPARATE_QUEUED], separate);
            observer.observe(NexusInstruments.INVOCATION_EXECUTING, sum[EXECUTING], normal);
            observer.observe(NexusInstruments.INVOCATION_EXECUTING, sum[SEPARATE_EXECUTING], separate);
            observer.observe(NexusInstruments.INVOCATION_CREDIT_WAITS, sum[CREDIT_WAITS], tags);
            observer.observe(NexusInstruments.LARGE_MESSAGE_IN_FLIGHT, sum[LARGE_INBOUND],
                with(tags, MetricTags.DIRECTION, "inbound"));
            observer.observe(NexusInstruments.LARGE_MESSAGE_IN_FLIGHT, sum[LARGE_OUTBOUND],
                with(tags, MetricTags.DIRECTION, "outbound"));
        }
    }

    private static final int IO_FRAMES_SENT = 0;
    private static final int IO_BYTES_SENT = 1;
    private static final int IO_FRAMES_RECEIVED = 2;
    private static final int IO_BYTES_RECEIVED = 3;
    private static final int ACTIVE = 4;
    private static final int HANDSHAKING = 5;
    private static final int BUDGET_RESERVED = 6;
    private static final int BUDGET_LIMIT = 7;
    private static final int PIPES_OPEN = 8;
    private static final int PIPE_WINDOW = 9;
    private static final int QUEUED = 10;
    private static final int SEPARATE_QUEUED = 11;
    private static final int EXECUTING = 12;
    private static final int SEPARATE_EXECUTING = 13;
    private static final int CREDIT_WAITS = 14;
    private static final int LARGE_INBOUND = 15;
    private static final int LARGE_OUTBOUND = 16;
    private static final int SUMS = 17;

    private static long[] sumFor(Map<Map<String, String>, long[]> sums, Map<String, String> tags) {
        long[] sum = sums.get(tags);
        if (sum == null) {
            sum = new long[SUMS];
            sums.put(tags, sum);
        }
        return sum;
    }

    private static Map<String, String> with(Map<String, String> tags, String key, String value) {
        Map<String, String> result = new LinkedHashMap<>(tags);
        result.put(key, value);
        return Collections.unmodifiableMap(result);
    }

    /** Stops tracking every client and removes every listener. */
    @Override
    public void close() {
        List<NexusClient<?>> all;
        synchronized (clients) {
            all = new ArrayList<>(clients.keySet());
        }
        for (NexusClient<?> client : all) {
            untrack(client);
        }
        listeners.clear();
    }

    /** A session started (its transport connected): counted when it belongs to a tracked client. */
    private void onSessionStarted(NexusSession session) {
        Object owner = session.owner();
        if (owner == null) {
            return;
        }
        Tracked tracked;
        synchronized (clients) {
            tracked = clients.get(owner);
        }
        if (tracked != null) {
            attach(tracked, session);
        }
    }

    private void attach(Tracked tracked, NexusSession session) {
        synchronized (clients) {
            if (clients.get(tracked.client) != tracked || tracked.session == session) {
                return;
            }
            retireLocked(tracked);
            tracked.session = session;
            tracked.sink = session.addMetricsSink((instrument, value, extra) -> emit(instrument, value,
                extra.isEmpty() ? tracked.tags : merge(tracked.tags, extra)));
            MetricsCounters.SUBSCRIPTIONS.incrementAndGet();
        }
        session.closedInternal().whenComplete((reason, error) ->
            onEnded(tracked, session, reason == null ? DisconnectReason.SOCKET_ERROR : reason));
    }

    /** The session ended: records its duration (from the transport connection) and its disconnect, in C# order. */
    private void onEnded(Tracked tracked, NexusSession session, DisconnectReason reason) {
        synchronized (clients) {
            if (tracked.session != session) {
                return;
            }
            retireLocked(tracked);
        }
        double seconds = Math.max(0, session.nanoTime() - session.connectedNanos()) / 1_000_000_000.0;
        String reasonTag = MetricTags.disconnectReasonTag(reason);
        Map<String, String> durationTags = with(tracked.tags, MetricTags.DISCONNECT_REASON, reasonTag);
        Map<String, String> countTags = with(durationTags, MetricTags.PHASE,
            session.isEstablished() ? "established" : "handshake");
        emit(NexusInstruments.SESSION_DURATION, seconds, durationTags);
        emit(NexusInstruments.SESSION_DISCONNECTS, 1, countTags);
    }

    /** Stops counting the tracked session: detaches its sink and keeps its transport totals. */
    private void retireLocked(Tracked tracked) {
        NexusSession session = tracked.session;
        if (session == null) {
            return;
        }
        tracked.session = null;
        Registration sink = tracked.sink;
        tracked.sink = null;
        if (sink != null) {
            sink.close();
            MetricsCounters.SUBSCRIPTIONS.decrementAndGet();
        }
        long[] totals = retired.get(tracked.tags);
        if (totals == null) {
            totals = new long[4];
            retired.put(tracked.tags, totals);
        }
        totals[IO_FRAMES_SENT] += session.framesSent();
        totals[IO_BYTES_SENT] += session.bytesSent();
        totals[IO_FRAMES_RECEIVED] += session.framesReceived();
        totals[IO_BYTES_RECEIVED] += session.bytesReceived();
    }

    private static Map<String, String> merge(Map<String, String> tags, Map<String, String> extra) {
        Map<String, String> result = new LinkedHashMap<>(tags);
        result.putAll(extra);
        return Collections.unmodifiableMap(result);
    }

    private void emit(NexusInstrument instrument, double value, Map<String, String> tags) {
        boolean counter = instrument.kind() == InstrumentKind.COUNTER;
        for (NexusMetricsListener listener : listeners) {
            try {
                if (counter) {
                    listener.add(instrument, (long) value, tags);
                } else {
                    listener.record(instrument, value, tags);
                }
            } catch (RuntimeException ignored) {
                // A listener must never break the client.
            }
        }
    }
}
