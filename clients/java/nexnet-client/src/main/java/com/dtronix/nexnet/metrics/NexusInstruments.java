package com.dtronix.nexnet.metrics;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * Every instrument of the C# {@code NexNet} meter, with identical names, kinds, units and tags
 * (docs/articles/metrics.md). Server-only instruments are listed for completeness; a client never reports them.
 */
public final class NexusInstruments {
    /** The meter name (C# {@code NexusMeter.Name}). */
    public static final String METER_NAME = "NexNet";

    private static final double[] NONE = {};
    private static final double[] SESSION_BUCKETS = {0.1, 1, 10, 60, 300, 1800, 3600, 4 * 3600, 24 * 3600};
    private static final double[] WAIT_BUCKETS = {0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60};

    /** Sessions open, from transport connection until disconnect. */
    public static final NexusInstrument SESSION_ACTIVE = new NexusInstrument("nexnet.session.active",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{session}",
        "Sessions open, from transport connection until disconnect.",
        Collections.<String>emptyList(), false, NONE);

    /** Sessions that have not completed the handshake (and, on a server, authentication). */
    public static final NexusInstrument SESSION_HANDSHAKING = new NexusInstrument("nexnet.session.handshaking",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{session}",
        "Sessions that have not completed the handshake (and, on a server, authentication).",
        Collections.<String>emptyList(), false, NONE);

    /** Sessions ended, by disconnect reason and by whether the handshake had completed. */
    public static final NexusInstrument SESSION_DISCONNECTS = new NexusInstrument("nexnet.session.disconnects",
        InstrumentKind.COUNTER, "{session}",
        "Sessions ended, by disconnect reason and by whether the handshake had completed.",
        Arrays.asList(MetricTags.DISCONNECT_REASON, MetricTags.PHASE), false, NONE);

    /** Time from transport connection to disconnect. */
    public static final NexusInstrument SESSION_DURATION = new NexusInstrument("nexnet.session.duration",
        InstrumentKind.HISTOGRAM, "s",
        "Time from transport connection to disconnect.",
        Arrays.asList(MetricTags.DISCONNECT_REASON), false, SESSION_BUCKETS);

    /** Connections a server closed before starting a session: handshake limit or rate limiting. */
    public static final NexusInstrument CONNECTION_REJECTIONS = new NexusInstrument("nexnet.connection.rejections",
        InstrumentKind.COUNTER, "{connection}",
        "Connections a server closed before starting a session: handshake limit or rate limiting.",
        Arrays.asList(MetricTags.REJECTION_REASON), true, NONE);

    /** Frames written to and read from the transport. */
    public static final NexusInstrument IO_FRAMES = new NexusInstrument("nexnet.io.frames",
        InstrumentKind.OBSERVABLE_COUNTER, "{frame}",
        "Frames written to and read from the transport.",
        Arrays.asList(MetricTags.DIRECTION), false, NONE);

    /** Bytes written to and read from the transport, frame headers included. */
    public static final NexusInstrument IO_BYTES = new NexusInstrument("nexnet.io.bytes",
        InstrumentKind.OBSERVABLE_COUNTER, "By",
        "Bytes written to and read from the transport, frame headers included.",
        Arrays.asList(MetricTags.DIRECTION), false, NONE);

    /** Session budget reserved: pipe windows, invocation queues and large values. */
    public static final NexusInstrument BUDGET_RESERVED = new NexusInstrument("nexnet.budget.reserved",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "By",
        "Session budget reserved: pipe windows, invocation queues and large values.",
        Collections.<String>emptyList(), false, NONE);

    /** Session budget in total. */
    public static final NexusInstrument BUDGET_LIMIT = new NexusInstrument("nexnet.budget.limit",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "By",
        "Session budget in total.",
        Collections.<String>emptyList(), false, NONE);

    /** Pipes holding a receive window: open, or closing until both sides confirmed. */
    public static final NexusInstrument PIPE_OPEN = new NexusInstrument("nexnet.pipe.open",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{pipe}",
        "Pipes holding a receive window: open, or closing until both sides confirmed.",
        Collections.<String>emptyList(), false, NONE);

    /** Receive windows reserved by open pipes. */
    public static final NexusInstrument PIPE_WINDOW = new NexusInstrument("nexnet.pipe.window",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "By",
        "Receive windows reserved by open pipes.",
        Collections.<String>emptyList(), false, NONE);

    /** Pipes reset because the peer sent a channel item that breaks the framing or exceeds {@code MaxMessageSize}. */
    public static final NexusInstrument PIPE_RESETS = new NexusInstrument("nexnet.pipe.resets",
        InstrumentKind.COUNTER, "{pipe}",
        "Pipes reset because the peer sent a channel item that breaks the framing or exceeds MaxMessageSize.",
        Arrays.asList(MetricTags.RESET_REASON), false, NONE);

    /** Pipe opens refused because a session limit was reached. */
    public static final NexusInstrument PIPE_REFUSALS = new NexusInstrument("nexnet.pipe.refusals",
        InstrumentKind.COUNTER, "{pipe}",
        "Pipe opens refused because a session limit was reached.",
        Arrays.asList(MetricTags.REFUSAL_SIDE), false, NONE);

    /** Invocations received and waiting for an execution slot, by invocation pool. */
    public static final NexusInstrument INVOCATION_QUEUED = new NexusInstrument("nexnet.invocation.queued",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{invocation}",
        "Invocations received and waiting for an execution slot, by invocation pool.",
        Arrays.asList(MetricTags.INVOCATION_POOL), false, NONE);

    /** Invocations running, by invocation pool. */
    public static final NexusInstrument INVOCATION_EXECUTING = new NexusInstrument("nexnet.invocation.executing",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{invocation}",
        "Invocations running, by invocation pool.",
        Arrays.asList(MetricTags.INVOCATION_POOL), false, NONE);

    /** Calls waiting for the peer to return invocation credit. */
    public static final NexusInstrument INVOCATION_CREDIT_WAITS = new NexusInstrument("nexnet.invocation.credit_waits",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{invocation}",
        "Calls waiting for the peer to return invocation credit.",
        Collections.<String>emptyList(), false, NONE);

    /** Invocations to several sessions that found a session busy on the first pass. */
    public static final NexusInstrument INVOCATION_BUSY = new NexusInstrument("nexnet.invocation.busy",
        InstrumentKind.COUNTER, "{invocation}",
        "Invocations to several sessions that found a session busy on the first pass.",
        Collections.<String>emptyList(), true, NONE);

    /** Invocations skipped because the peer returned no invocation credit in time. */
    public static final NexusInstrument INVOCATION_CREDIT_DROPS = new NexusInstrument("nexnet.invocation.credit_drops",
        InstrumentKind.COUNTER, "{invocation}",
        "Invocations skipped because the peer returned no invocation credit in time.",
        Collections.<String>emptyList(), false, NONE);

    /** Time an invocation waited from being queued until it took an execution slot. */
    public static final NexusInstrument INVOCATION_QUEUE_WAIT = new NexusInstrument("nexnet.invocation.queue_wait",
        InstrumentKind.HISTOGRAM, "s",
        "Time an invocation waited from being queued until it took an execution slot.",
        Arrays.asList(MetricTags.INVOCATION_POOL), false, WAIT_BUCKETS);

    /** Time a pipe writer waited for the peer to grant window credit, recorded when the wait ends. */
    public static final NexusInstrument PIPE_CREDIT_WAIT = new NexusInstrument("nexnet.pipe.credit_wait",
        InstrumentKind.HISTOGRAM, "s",
        "Time a pipe writer waited for the peer to grant window credit, recorded when the wait ends.",
        Collections.<String>emptyList(), false, WAIT_BUCKETS);

    /** Arguments and results larger than one frame being received or sent. */
    public static final NexusInstrument LARGE_MESSAGE_IN_FLIGHT = new NexusInstrument("nexnet.large_message.in_flight",
        InstrumentKind.OBSERVABLE_UP_DOWN_COUNTER, "{message}",
        "Arguments and results larger than one frame being received or sent.",
        Arrays.asList(MetricTags.DIRECTION), false, NONE);

    /** Arguments and results larger than one frame stopped before completing. */
    public static final NexusInstrument LARGE_MESSAGE_ABORTS = new NexusInstrument("nexnet.large_message.aborts",
        InstrumentKind.COUNTER, "{message}",
        "Arguments and results larger than one frame stopped before completing.",
        Arrays.asList(MetricTags.DIRECTION, MetricTags.ABORT_REASON), false, NONE);

    private static final List<NexusInstrument> ALL = Collections.unmodifiableList(Arrays.asList(
        SESSION_ACTIVE, SESSION_HANDSHAKING, SESSION_DISCONNECTS, SESSION_DURATION, CONNECTION_REJECTIONS, 
        IO_FRAMES, IO_BYTES, BUDGET_RESERVED, BUDGET_LIMIT, PIPE_OPEN, PIPE_WINDOW, PIPE_RESETS, 
        PIPE_REFUSALS, INVOCATION_QUEUED, INVOCATION_EXECUTING, INVOCATION_CREDIT_WAITS, INVOCATION_BUSY, 
        INVOCATION_CREDIT_DROPS, INVOCATION_QUEUE_WAIT, PIPE_CREDIT_WAIT, LARGE_MESSAGE_IN_FLIGHT, 
        LARGE_MESSAGE_ABORTS));

    private NexusInstruments() {
    }

    /**
     * Every instrument, in the order of the C# meter.
     *
     * @return the instruments
     */
    public static List<NexusInstrument> all() {
        return ALL;
    }
}
