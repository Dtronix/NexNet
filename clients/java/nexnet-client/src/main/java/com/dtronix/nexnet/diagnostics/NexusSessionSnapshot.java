package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.client.ConnectionState;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * Live state of one session, read when {@link Diagnostics#snapshot} was called. Mirrors the C#
 * {@code NexusSessionSnapshot}: the same fields, in the same order, camelCased. Durations are {@link NexusTimeSpan}s.
 *
 * <p>A value this runtime does not track yet is null; see each accessor. The values are read one at a time, so they
 * are not an atomic picture. A snapshot exposes internal state (IDs, sizes, timings): do not serve it to
 * unauthenticated callers.
 */
public final class NexusSessionSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "id", "isServer", "transport", "endpoint", "isSecure", "state", "isEstablished", "connected",
        "sinceLastReceived", "sinceLastSent", "flushInProgress", "budget", "invocations", "pipes",
        "largeMessages", "framesSent", "bytesSent", "framesReceived", "bytesReceived"));

    private final long id;
    private final boolean isServer;
    private final String transport;
    private final @Nullable String endpoint;
    private final @Nullable Boolean isSecure;
    private final ConnectionState state;
    private final boolean isEstablished;
    private final @Nullable NexusTimeSpan connected;
    private final @Nullable NexusTimeSpan sinceLastReceived;
    private final @Nullable NexusTimeSpan sinceLastSent;
    private final @Nullable NexusTimeSpan flushInProgress;
    private final @Nullable NexusBudgetSnapshot budget;
    private final @Nullable NexusInvocationSnapshot invocations;
    private final List<NexusPipeSnapshot> pipes;
    private final List<NexusLargeMessageSnapshot> largeMessages;
    private final @Nullable Long framesSent;
    private final @Nullable Long bytesSent;
    private final @Nullable Long framesReceived;
    private final @Nullable Long bytesReceived;

    /**
     * Creates a snapshot.
     *
     * @param id the session ID
     * @param isServer true for a server session; always false in this client
     * @param transport the transport name, as in the {@code nexnet.transport} metric tag
     * @param endpoint the server endpoint name; null for a client session
     * @param isSecure on a client, whether the transport is encrypted; null on a server session
     * @param state the connection state
     * @param isEstablished true once the handshake completed
     * @param connected the time since the transport connected, or null when not tracked
     * @param sinceLastReceived the time since a frame was last received, or null when not tracked
     * @param sinceLastSent the time since data was last written, or null when nothing was or it is not tracked
     * @param flushInProgress how long the flush in progress has run, or null when none runs or it is not tracked
     * @param budget the session budget, or null when not tracked
     * @param invocations the invocations in both directions, or null when not tracked
     * @param pipes the pipes holding a receive window
     * @param largeMessages the arguments and results larger than one frame in transfer
     * @param framesSent the frames written, or null when not tracked
     * @param bytesSent the bytes written, or null when not tracked
     * @param framesReceived the frames read, or null when not tracked
     * @param bytesReceived the bytes read, or null when not tracked
     */
    public NexusSessionSnapshot(long id, boolean isServer, String transport, @Nullable String endpoint, @Nullable Boolean isSecure, ConnectionState state, boolean isEstablished, @Nullable NexusTimeSpan connected, @Nullable NexusTimeSpan sinceLastReceived, @Nullable NexusTimeSpan sinceLastSent, @Nullable NexusTimeSpan flushInProgress, @Nullable NexusBudgetSnapshot budget, @Nullable NexusInvocationSnapshot invocations, List<NexusPipeSnapshot> pipes, List<NexusLargeMessageSnapshot> largeMessages, @Nullable Long framesSent, @Nullable Long bytesSent, @Nullable Long framesReceived, @Nullable Long bytesReceived) {
        this.id = id;
        this.isServer = isServer;
        this.transport = Objects.requireNonNull(transport, "transport");
        this.endpoint = endpoint;
        this.isSecure = isSecure;
        this.state = Objects.requireNonNull(state, "state");
        this.isEstablished = isEstablished;
        this.connected = connected;
        this.sinceLastReceived = sinceLastReceived;
        this.sinceLastSent = sinceLastSent;
        this.flushInProgress = flushInProgress;
        this.budget = budget;
        this.invocations = invocations;
        this.pipes = Collections.unmodifiableList(new ArrayList<>(pipes));
        this.largeMessages = Collections.unmodifiableList(new ArrayList<>(largeMessages));
        this.framesSent = framesSent;
        this.bytesSent = bytesSent;
        this.framesReceived = framesReceived;
        this.bytesReceived = bytesReceived;
    }

    /**
     * The session ID.
     *
     * @return the value
     */
    public long id() {
        return id;
    }

    /**
     * True for a server session; always false in this client.
     *
     * @return the value
     */
    public boolean isServer() {
        return isServer;
    }

    /**
     * The transport name, as in the {@code nexnet.transport} metric tag.
     *
     * @return the value
     */
    public String transport() {
        return transport;
    }

    /**
     * The server endpoint name; null for a client session.
     *
     * @return the value
     */
    public @Nullable String endpoint() {
        return endpoint;
    }

    /**
     * On a client, whether the transport is encrypted; null on a server session.
     *
     * @return the value
     */
    public @Nullable Boolean isSecure() {
        return isSecure;
    }

    /**
     * The connection state.
     *
     * @return the value
     */
    public ConnectionState state() {
        return state;
    }

    /**
     * True once the handshake completed.
     *
     * @return the value
     */
    public boolean isEstablished() {
        return isEstablished;
    }

    /**
     * The time since the transport connected, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan connected() {
        return connected;
    }

    /**
     * The time since a frame was last received, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan sinceLastReceived() {
        return sinceLastReceived;
    }

    /**
     * The time since data was last written, or null when nothing was or it is not tracked.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan sinceLastSent() {
        return sinceLastSent;
    }

    /**
     * How long the flush in progress has run, or null when none runs or it is not tracked.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan flushInProgress() {
        return flushInProgress;
    }

    /**
     * The session budget, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable NexusBudgetSnapshot budget() {
        return budget;
    }

    /**
     * The invocations in both directions, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable NexusInvocationSnapshot invocations() {
        return invocations;
    }

    /**
     * The pipes holding a receive window.
     *
     * @return the value
     */
    public List<NexusPipeSnapshot> pipes() {
        return pipes;
    }

    /**
     * The arguments and results larger than one frame in transfer.
     *
     * @return the value
     */
    public List<NexusLargeMessageSnapshot> largeMessages() {
        return largeMessages;
    }

    /**
     * The frames written, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable Long framesSent() {
        return framesSent;
    }

    /**
     * The bytes written, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable Long bytesSent() {
        return bytesSent;
    }

    /**
     * The frames read, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable Long framesReceived() {
        return framesReceived;
    }

    /**
     * The bytes read, or null when not tracked.
     *
     * @return the value
     */
    public @Nullable Long bytesReceived() {
        return bytesReceived;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusSessionSnapshot)) {
            return false;
        }
        NexusSessionSnapshot o = (NexusSessionSnapshot) obj;
        return id == o.id
            && isServer == o.isServer
            && Objects.equals(transport, o.transport)
            && Objects.equals(endpoint, o.endpoint)
            && Objects.equals(isSecure, o.isSecure)
            && Objects.equals(state, o.state)
            && isEstablished == o.isEstablished
            && Objects.equals(connected, o.connected)
            && Objects.equals(sinceLastReceived, o.sinceLastReceived)
            && Objects.equals(sinceLastSent, o.sinceLastSent)
            && Objects.equals(flushInProgress, o.flushInProgress)
            && Objects.equals(budget, o.budget)
            && Objects.equals(invocations, o.invocations)
            && Objects.equals(pipes, o.pipes)
            && Objects.equals(largeMessages, o.largeMessages)
            && Objects.equals(framesSent, o.framesSent)
            && Objects.equals(bytesSent, o.bytesSent)
            && Objects.equals(framesReceived, o.framesReceived)
            && Objects.equals(bytesReceived, o.bytesReceived);
    }

    @Override
    public int hashCode() {
        return Objects.hash(id, isServer, transport, endpoint, isSecure, state, isEstablished, connected, sinceLastReceived, sinceLastSent, flushInProgress, budget, invocations, pipes, largeMessages, framesSent, bytesSent, framesReceived, bytesReceived);
    }

    @Override
    public String toString() {
        return "NexusSessionSnapshot[" + "id=" + id
            + ", isServer=" + isServer
            + ", transport=" + transport
            + ", endpoint=" + endpoint
            + ", isSecure=" + isSecure
            + ", state=" + state
            + ", isEstablished=" + isEstablished
            + ", connected=" + connected
            + ", sinceLastReceived=" + sinceLastReceived
            + ", sinceLastSent=" + sinceLastSent
            + ", flushInProgress=" + flushInProgress
            + ", budget=" + budget
            + ", invocations=" + invocations
            + ", pipes=" + pipes
            + ", largeMessages=" + largeMessages
            + ", framesSent=" + framesSent
            + ", bytesSent=" + bytesSent
            + ", framesReceived=" + framesReceived
            + ", bytesReceived=" + bytesReceived + "]";
    }
}
