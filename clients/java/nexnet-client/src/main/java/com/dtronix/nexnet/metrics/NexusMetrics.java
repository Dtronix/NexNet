package com.dtronix.nexnet.metrics;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.session.NexusSession;
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
 * {@link #track}; listeners ({@link NexusMetricsListener}) receive {@code nexnet.session.disconnects} and
 * {@code nexnet.session.duration} as sessions end, and {@link #collect} reads the observable instruments.
 *
 * <p>Reported from the client's public state: {@code nexnet.session.duration} starts when the session is seen
 * established (C# starts at the transport connection), and {@code nexnet.session.handshaking} counts clients that
 * are connecting. The transport, budget, invocation, pipe and large-message instruments are cataloged but not
 * reported until the session and invocation layers expose their counters.
 *
 * <p>Thread-safe.
 */
public final class NexusMetrics implements AutoCloseable {
    private static final Object SHARED_LOCK = new Object();
    private static @Nullable NexusMetrics shared;

    private final SessionTimers clock;
    private final CopyOnWriteArrayList<NexusMetricsListener> listeners = new CopyOnWriteArrayList<>();
    private final Map<NexusClient<?>, Tracked> clients = new IdentityHashMap<>();

    /** One tracked client. */
    private final class Tracked {
        final NexusClient<?> client;
        final Map<String, String> tags;
        @Nullable Registration stateRegistration;
        @Nullable NexusSession session;
        long establishedNanos;
        boolean detached;

        Tracked(NexusClient<?> client) {
            this.client = client;
            Map<String, String> t = new LinkedHashMap<>();
            t.put(MetricTags.ROLE, "client");
            t.put(MetricTags.TRANSPORT, MetricTags.transportTag(client.config()));
            this.tags = Collections.unmodifiableMap(t);
        }
    }

    /** Creates an instance on the system clock. */
    public NexusMetrics() {
        this(SessionTimers.system());
    }

    /**
     * Creates an instance.
     *
     * @param clock the clock durations are measured on
     */
    public NexusMetrics(SessionTimers clock) {
        if (clock == null) {
            throw new NullPointerException("clock");
        }
        this.clock = clock;
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
     * Starts reporting a client. Tracking a client twice is a no-op.
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
        }
        MetricsCounters.SUBSCRIPTIONS.incrementAndGet();
        tracked.stateRegistration = client.addStateListener(state -> {
            if (state == ConnectionState.CONNECTED) {
                onEstablished(tracked);
            }
        });
        onEstablished(tracked);
        return () -> untrack(client);
    }

    /**
     * Stops reporting a client.
     *
     * @param client the client
     */
    public void untrack(NexusClient<?> client) {
        Tracked tracked;
        synchronized (clients) {
            tracked = clients.remove(client);
            if (tracked == null) {
                return;
            }
            tracked.detached = true;
            tracked.session = null;
        }
        Registration registration = tracked.stateRegistration;
        if (registration != null) {
            registration.close();
        }
        MetricsCounters.SUBSCRIPTIONS.decrementAndGet();
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
     * Reads the observable instruments: one measurement per instrument and tag set.
     *
     * @param observer receives the measurements
     */
    public void collect(MeasurementObserver observer) {
        Map<Map<String, String>, long[]> sums = new LinkedHashMap<>();
        List<Tracked> snapshot;
        synchronized (clients) {
            snapshot = new ArrayList<>(clients.values());
        }
        for (Tracked tracked : snapshot) {
            long[] sum = sums.get(tracked.tags);
            if (sum == null) {
                sum = new long[2];
                sums.put(tracked.tags, sum);
            }
            ConnectionState state = tracked.client.state();
            NexusSession session;
            synchronized (clients) {
                session = tracked.session;
            }
            if (session != null && !session.isClosing()) {
                sum[0]++;
            } else if (state == ConnectionState.CONNECTING || state == ConnectionState.RECONNECTING) {
                sum[0]++;
                sum[1]++;
            }
        }
        for (Map.Entry<Map<String, String>, long[]> entry : sums.entrySet()) {
            observer.observe(NexusInstruments.SESSION_ACTIVE, entry.getValue()[0], entry.getKey());
            observer.observe(NexusInstruments.SESSION_HANDSHAKING, entry.getValue()[1], entry.getKey());
        }
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

    private void onEstablished(Tracked tracked) {
        NexusSession session = tracked.client.session();
        if (session == null) {
            return;
        }
        synchronized (clients) {
            if (tracked.detached || tracked.session == session) {
                return;
            }
            tracked.session = session;
            tracked.establishedNanos = clock.nanoTime();
        }
        session.closed().whenComplete((reason, error) ->
            onEnded(tracked, session, reason == null ? DisconnectReason.SOCKET_ERROR : reason));
    }

    private void onEnded(Tracked tracked, NexusSession session, DisconnectReason reason) {
        double seconds;
        synchronized (clients) {
            if (tracked.detached || tracked.session != session) {
                return;
            }
            tracked.session = null;
            seconds = Math.max(0, clock.nanoTime() - tracked.establishedNanos) / 1_000_000_000.0;
        }
        String reasonTag = MetricTags.disconnectReasonTag(reason);
        Map<String, String> disconnectTags = new LinkedHashMap<>(tracked.tags);
        disconnectTags.put(MetricTags.DISCONNECT_REASON, reasonTag);
        Map<String, String> durationTags = Collections.unmodifiableMap(new LinkedHashMap<>(disconnectTags));
        disconnectTags.put(MetricTags.PHASE, "established");
        Map<String, String> countTags = Collections.unmodifiableMap(disconnectTags);
        for (NexusMetricsListener listener : listeners) {
            try {
                listener.add(NexusInstruments.SESSION_DISCONNECTS, 1, countTags);
                listener.record(NexusInstruments.SESSION_DURATION, seconds, durationTags);
            } catch (RuntimeException ignored) {
                // A listener must never break the client.
            }
        }
    }
}
