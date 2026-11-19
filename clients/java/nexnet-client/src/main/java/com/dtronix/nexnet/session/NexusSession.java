package com.dtronix.nexnet.session;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.transport.Transport;
import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.security.cert.X509Certificate;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import org.jspecify.annotations.Nullable;

/**
 * One client connection: the NexNet framing, handshake, ping, timeouts and disconnect handling over a
 * {@link Transport}. {@code NexusClient} creates one per connection attempt; the invocation, pipe and collection
 * runtimes attach through {@link #setMessageHandler(MessageHandler)}, {@link #send(Frame)} and
 * {@link #controlSender()}.
 *
 * <p>Threads. Each session has one blocking <b>reader</b> thread ({@code nexnet-<name>-reader}) that parses frames
 * and dispatches them synchronously, and one <b>writer</b> thread ({@code nexnet-<name>-writer}) that drains a
 * bounded frame queue and the {@link ControlSender}; both are daemons. Socket IO never happens under the session
 * lock, and the reader thread never waits on a send.
 *
 * <p>Futures. {@link #closed()} completes on the callback executor. The other futures returned here
 * ({@link #start()}, {@link #send(Frame)}, {@link #closedInternal()}) are runtime-internal: they complete on the
 * reader, writer or timer thread, so the runtime must hop to an executor before handing anything to user code.
 *
 * <p>The protocol rules implemented here are in {@code docs/internals/protocol-specification.md}, sections 1, 2, 5
 * and 9.
 */
public final class NexusSession {
    /** The connection preamble: magic {@code NnP\x14}, three reserved zero bytes, protocol version 1. */
    private static final byte[] PREAMBLE = {0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1};
    /** Ping interval when none is configured and the server advertises no idle timeout (C# default). */
    public static final int DEFAULT_PING_INTERVAL_MILLIS = 10_000;
    /** Shortest ping interval derived from the server's advertised idle timeout (C# default). */
    public static final int MIN_DERIVED_PING_INTERVAL_MILLIS = 1_000;
    /** The largest body accepted before the connection completes. */
    public static final int MAX_PRE_AUTH_BODY_LENGTH = 16 * 1024;
    /** The largest authentication token the server accepts. */
    public static final int MAX_AUTHENTICATION_TOKEN_LENGTH = 8192;

    private enum Phase { NEW, HANDSHAKING, CONNECTED, CLOSING, CLOSED }

    /** A queued frame and the future completed once it has been written. */
    private static final class Pending {
        final Frame frame;
        final @Nullable CompletableFuture<Void> done;

        Pending(Frame frame, @Nullable CompletableFuture<Void> done) {
            this.frame = frame;
            this.done = done;
        }
    }

    private final String name;
    private final Transport transport;
    private final HandshakeDescriptor handshake;
    private final byte[] authenticationToken;
    private final boolean reconnecting;
    private final SessionOptions options;
    private final int handshakeTimeoutMillis;
    private final long timeoutNanos;
    private final long progressTimeoutNanos;
    private final @Nullable Integer pingIntervalMillis;
    private final Executor callbackExecutor;
    private final SessionTimers timers;
    private final @Nullable NexusLogger logger;
    private final ControlSender controlSender;

    private final Object lock = new Object();
    private final ArrayDeque<Pending> queue = new ArrayDeque<>();
    private final List<ProgressProbe> probes = new CopyOnWriteArrayList<>();
    private Phase phase = Phase.NEW;
    private DisconnectReason disconnectReason = DisconnectReason.NONE;
    private @Nullable DisconnectReason terminal;
    private boolean stopWriter;
    private @Nullable Thread readerThread;
    private @Nullable Thread writerThread;
    private @Nullable Registration handshakeTimer;
    private @Nullable Registration pingTimer;
    private @Nullable Registration closeFallback;

    private final AtomicBoolean finished = new AtomicBoolean();
    private final CompletableFuture<Void> ready = new CompletableFuture<>();
    private final CompletableFuture<DisconnectReason> closedInternal = new CompletableFuture<>();
    private final CompletableFuture<DisconnectReason> closedPublic = new CompletableFuture<>();

    private volatile MessageHandler handler = MessageHandler.UNHANDLED;
    private volatile long lastReceivedNanos;
    private volatile long sessionId;
    private volatile int peerMaxMessageSize = -1;
    private volatile int peerInvocationCredits = -1;
    private volatile long advertisedIdleTimeoutMillis = -1;
    private volatile long effectivePingIntervalMillis = -1;
    /** True while the writer thread is inside a transport write or flush (the progress check, section 9). */
    private volatile boolean writing;
    /** The clock reading when the write in progress started; meaningful only while {@code writing}. */
    private volatile long writeStartedNanos;

    private NexusSession(Builder b) {
        this.name = b.name;
        this.transport = b.transport;
        this.handshake = b.handshake;
        this.authenticationToken = b.authenticationToken;
        this.reconnecting = b.reconnecting;
        this.options = b.options;
        this.handshakeTimeoutMillis = b.handshakeTimeoutMillis;
        this.timeoutNanos = TimeUnit.MILLISECONDS.toNanos(b.timeoutMillis);
        this.progressTimeoutNanos = TimeUnit.MILLISECONDS.toNanos(b.progressTimeoutMillis);
        this.pingIntervalMillis = b.pingIntervalMillis;
        this.callbackExecutor = b.callbackExecutor;
        this.timers = b.timers;
        this.logger = b.logger;
        this.controlSender = new ControlSender(this::wakeWriter, options.invocationCredits());
    }

    /**
     * Starts building a session.
     *
     * @param transport the connected transport
     * @param handshake the generated handshake constants
     * @return a builder
     */
    public static Builder builder(Transport transport, HandshakeDescriptor handshake) {
        return new Builder(transport, handshake);
    }

    // ------------------------------------------------------------------ lifecycle

    /**
     * Starts the writer and reader threads, writes the preamble and the greeting, and starts the handshake timeout.
     * Runtime-internal future: it completes on the reader thread when a valid {@code ServerGreeting} arrives, or fails
     * with {@link SessionClosedException} when the session closes first.
     *
     * @return the handshake future
     * @throws IllegalStateException when already started
     */
    public CompletableFuture<Void> start() {
        Thread writer;
        Thread reader;
        synchronized (lock) {
            if (phase != Phase.NEW) {
                throw new IllegalStateException("The session has already started.");
            }
            phase = Phase.HANDSHAKING;
            queue.add(new Pending(Frame.raw(-1, PREAMBLE.clone()), null));
            queue.add(new Pending(Frame.of(reconnecting ? MessageTypes.CLIENT_GREETING_RECONNECTION
                : MessageTypes.CLIENT_GREETING, clientGreetingBody()), null));
            SessionCounters.PENDING_WRITES.addAndGet(2);
            writer = new Thread(this::writeLoop, "nexnet-" + name + "-writer");
            reader = new Thread(this::readLoop, "nexnet-" + name + "-reader");
            writer.setDaemon(true);
            reader.setDaemon(true);
            writerThread = writer;
            readerThread = reader;
        }
        SessionCounters.OPEN_SESSIONS.incrementAndGet();
        SessionCounters.LIVE_THREADS.addAndGet(2);
        writer.start();
        reader.start();
        Registration timer = scheduleTimer(this::handshakeTimedOut, handshakeTimeoutMillis);
        synchronized (lock) {
            if (phase == Phase.HANDSHAKING) {
                handshakeTimer = timer;
                timer = null;
            }
        }
        if (timer != null) {
            timer.close();
        }
        log(NexusLogLevel.TRACE, "Session started.", null);
        return ready;
    }

    /**
     * Disconnects: sends the reason's disconnect byte, waits the disconnect delay and closes the transport. Does no
     * IO on the calling thread.
     *
     * @param reason the reason, normally {@link DisconnectReason#GRACEFUL}
     * @return the runtime-internal close future
     */
    public CompletableFuture<DisconnectReason> disconnect(DisconnectReason reason) {
        close(reason, true);
        return closedInternal;
    }

    /**
     * Closes at once without sending a disconnect byte: for an abandoned connection attempt (canceled or timed out).
     *
     * @param reason the reason recorded for the session
     */
    public void abort(DisconnectReason reason) {
        close(reason, false);
    }

    /**
     * Completes with the disconnect reason once the session has closed, on the callback executor.
     *
     * @return the future
     */
    public CompletableFuture<DisconnectReason> closed() {
        return closedPublic;
    }

    /**
     * Completes with the disconnect reason once the session has closed, on the thread that closed it.
     * Runtime-internal.
     *
     * @return the future
     */
    public CompletableFuture<DisconnectReason> closedInternal() {
        return closedInternal;
    }

    /**
     * Waits for the reader and writer threads to end, up to the configured join timeout. Returns at once when called
     * on one of them.
     *
     * @return true when both threads have ended
     * @throws InterruptedException when interrupted while waiting
     */
    public boolean awaitThreads() throws InterruptedException {
        Thread reader;
        Thread writer;
        synchronized (lock) {
            reader = readerThread;
            writer = writerThread;
        }
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(options.threadJoinTimeoutMillis());
        boolean ended = true;
        for (Thread thread : new Thread[] {writer, reader}) {
            if (thread == null) {
                continue;
            }
            if (thread == Thread.currentThread()) {
                ended = false;
                continue;
            }
            long left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime());
            if (left > 0) {
                thread.join(left);
            }
            ended &= !thread.isAlive();
        }
        return ended;
    }

    // ------------------------------------------------------------------ sending

    /**
     * Queues a frame for the writer thread. Runtime-internal future: it completes on the writer thread once the frame
     * has been written and flushed, or fails with {@link SessionClosedException}. When the queue is full the caller
     * blocks until there is room (never the reader thread, whose sends go through the {@link ControlSender}).
     *
     * @param frame the frame
     * @return the write future
     */
    public CompletableFuture<Void> send(Frame frame) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        DisconnectReason failure = null;
        synchronized (lock) {
            boolean isReader = Thread.currentThread() == readerThread;
            while (phase == Phase.CONNECTED && !isReader && queue.size() >= options.writeQueueCapacity()) {
                try {
                    lock.wait();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    done.completeExceptionally(e);
                    return done;
                }
            }
            if (phase == Phase.CONNECTED) {
                queue.add(new Pending(frame, done));
                SessionCounters.PENDING_WRITES.incrementAndGet();
                lock.notifyAll();
            } else {
                failure = phase.compareTo(Phase.CLOSING) >= 0 ? disconnectReason : DisconnectReason.NONE;
            }
        }
        if (failure == DisconnectReason.NONE) {
            done.completeExceptionally(new IllegalStateException("The session is not connected."));
        } else if (failure != null) {
            done.completeExceptionally(new SessionClosedException(failure));
        }
        return done;
    }

    /**
     * The coalescing sender for control messages.
     *
     * @return the control sender
     */
    public ControlSender controlSender() {
        return controlSender;
    }

    /** The handshake future returned by {@link #start()}; for tests. */
    CompletableFuture<Void> ready() {
        return ready;
    }

    /** The clock reading of the last complete frame; for tests. */
    long lastReceivedNanosForTest() {
        return lastReceivedNanos;
    }

    private void wakeWriter() {
        synchronized (lock) {
            lock.notifyAll();
        }
    }

    private void writeLoop() {
        List<Pending> batch = new ArrayList<>();
        List<Frame> control = new ArrayList<>();
        try {
            OutputStream out = new BufferedOutputStream(transport.output(), 16 * 1024);
            while (true) {
                DisconnectReason last;
                synchronized (lock) {
                    while (!stopWriter && terminal == null && queue.isEmpty()
                        && !(phase == Phase.CONNECTED && controlSender.hasPending())) {
                        lock.wait();
                    }
                    if (stopWriter) {
                        return;
                    }
                    last = terminal;
                    if (last == null) {
                        batch.addAll(queue);
                        queue.clear();
                        SessionCounters.PENDING_WRITES.addAndGet(-batch.size());
                        lock.notifyAll();
                    }
                }
                if (last != null) {
                    writeDisconnect(out, last);
                    return;
                }
                if (phase() == Phase.CONNECTED) {
                    controlSender.drainTo(control);
                }
                // Recorded for the progress check (specification section 9): a write that never completes is a
                // stall of the outbound transport.
                writeStartedNanos = timers.nanoTime();
                writing = true;
                try {
                    for (Pending pending : batch) {
                        out.write(pending.frame.bytes());
                    }
                    for (Frame frame : control) {
                        out.write(frame.bytes());
                    }
                    out.flush();
                } catch (IOException e) {
                    writing = false;
                    fail(batch, closingReasonOr(DisconnectReason.SOCKET_CLOSED_WHEN_WRITING));
                    if (phase().compareTo(Phase.CLOSING) < 0) {
                        log(NexusLogLevel.DEBUG, "Write failed.", e);
                        close(DisconnectReason.SOCKET_CLOSED_WHEN_WRITING, false);
                    }
                    return;
                }
                writing = false;
                for (Pending pending : batch) {
                    if (pending.done != null) {
                        pending.done.complete(null);
                    }
                }
                batch.clear();
                control.clear();
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            close(DisconnectReason.SOCKET_ERROR, false);
        } catch (RuntimeException e) {
            writing = false;
            log(NexusLogLevel.ERROR, "The writer failed.", e);
            fail(batch, DisconnectReason.PROTOCOL_ERROR);
            close(DisconnectReason.PROTOCOL_ERROR, false);
            finish();
        } finally {
            SessionCounters.LIVE_THREADS.decrementAndGet();
        }
    }

    /** Writes the disconnect byte and closes the transport after the disconnect delay. Nothing is sent after it. */
    private void writeDisconnect(OutputStream out, DisconnectReason reason) {
        List<Pending> dropped;
        synchronized (lock) {
            dropped = new ArrayList<>(queue);
            queue.clear();
            SessionCounters.PENDING_WRITES.addAndGet(-dropped.size());
            lock.notifyAll();
        }
        fail(dropped, reason);
        boolean sent = false;
        try {
            out.write(reason.code());
            out.flush();
            sent = true;
        } catch (IOException e) {
            log(NexusLogLevel.DEBUG, "Could not send the disconnect byte.", e);
        }
        int delay = options.disconnectDelayMillis();
        if (sent && delay > 0) {
            // A plain scheduler task, not a session timer: closing cancels the session timers.
            timers.schedule(this::finish, delay);
        } else {
            finish();
        }
    }

    private static void fail(List<Pending> pending, DisconnectReason reason) {
        for (Pending p : pending) {
            if (p.done != null) {
                p.done.completeExceptionally(new SessionClosedException(reason));
            }
        }
        pending.clear();
    }

    // ------------------------------------------------------------------ receiving

    private void readLoop() {
        try {
            FrameInput in = new FrameInput(transport.input());
            byte[] preamble = new byte[PREAMBLE.length];
            in.readFully(preamble, 0, preamble.length);
            for (int i = 0; i < PREAMBLE.length; i++) {
                if (preamble[i] != PREAMBLE[i]) {
                    throw new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR, "Bad preamble.");
                }
            }
            byte[] empty = new byte[0];
            while (true) {
                int type = in.read();
                if (type < 0) {
                    close(DisconnectReason.SOCKET_ERROR, false);
                    return;
                }
                if (MessageTypes.isDisconnect(type)) {
                    DisconnectReason reason = DisconnectReason.fromCode(type);
                    log(NexusLogLevel.INFORMATION, "The server disconnected: " + reason + " (" + type + ").", null);
                    close(reason, false);
                    return;
                }
                boolean completed = phase() == Phase.CONNECTED;
                if (type == MessageTypes.PING) {
                    if (!completed) {
                        throw protocolError("Ping before the connection completed.");
                    }
                    lastReceivedNanos = timers.nanoTime();
                    continue;
                }
                int postLength = MessageTypes.postHeaderLength(type);
                if (postLength < 0) {
                    throw protocolError("Unknown message type " + type + ".");
                }
                if (type == MessageTypes.CLIENT_GREETING || type == MessageTypes.CLIENT_GREETING_RECONNECTION) {
                    throw protocolError("The server sent a client greeting.");
                }
                if (!completed && type != MessageTypes.SERVER_GREETING) {
                    throw protocolError("Message type " + type + " before the connection completed.");
                }
                if (completed && type == MessageTypes.SERVER_GREETING) {
                    throw protocolError("A second ServerGreeting.");
                }
                int length = in.readLittleEndian(2);
                if (!completed && length > MAX_PRE_AUTH_BODY_LENGTH) {
                    throw protocolError("A " + length + "-byte body before the connection completed.");
                }
                int postHeader = in.readLittleEndian(postLength);
                byte[] body = length == 0 ? empty : new byte[length];
                in.readFully(body, 0, length);
                if (phase().compareTo(Phase.CLOSING) >= 0) {
                    return;
                }
                lastReceivedNanos = timers.nanoTime();
                if (type == MessageTypes.SERVER_GREETING) {
                    onServerGreeting(body);
                } else {
                    handler.onMessage(new InboundFrame(type, postHeader, body));
                }
            }
        } catch (SessionProtocolException e) {
            log(NexusLogLevel.INFORMATION, e.getMessage(), null);
            close(e.reason(), true);
        } catch (IOException e) {
            if (phase().compareTo(Phase.CLOSING) < 0) {
                log(NexusLogLevel.DEBUG, "Read ended: " + e, null);
                close(DisconnectReason.SOCKET_ERROR, false);
            }
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "The reader failed.", e);
            close(DisconnectReason.PROTOCOL_ERROR, true);
        } finally {
            SessionCounters.LIVE_THREADS.decrementAndGet();
        }
    }

    private static SessionProtocolException protocolError(String message) {
        return new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR, message);
    }

    /** Validates the ServerGreeting (specification section 5.2, client checks) and completes the connection. */
    private void onServerGreeting(byte[] body) {
        int version;
        long clientId;
        int maxMessageSize;
        int invocationCredits;
        long idleTimeoutMillis = -1;
        try {
            WireReader r = new WireReader(body, 0, body.length);
            int count = r.readArrayHeader();
            if (count < 4) {
                throw protocolError("ServerGreeting has " + count + " elements.");
            }
            version = r.readInt();
            clientId = r.readLong();
            maxMessageSize = r.readInt();
            invocationCredits = r.readInt();
            // S04 trailing field; older readers ignore trailing fields, and so does this one beyond it.
            if (count >= 5) {
                idleTimeoutMillis = r.readUInt32();
            }
            for (int i = 5; i < count; i++) {
                r.skip();
            }
            if (r.remaining() != 0) {
                throw protocolError("Trailing bytes after the ServerGreeting.");
            }
        } catch (WireFormatException e) {
            throw protocolError("ServerGreeting does not decode: " + e.getMessage());
        }
        if (version != handshake.clientNexusHash()) {
            throw new SessionProtocolException(DisconnectReason.CLIENT_MISMATCH,
                "The server expects another client nexus (hash " + version + ").");
        }
        if (maxMessageSize < SessionOptions.MIN_MAX_MESSAGE_SIZE || invocationCredits < 1) {
            throw protocolError("ServerGreeting limits out of range: maxMessageSize " + maxMessageSize
                + ", invocationCredits " + invocationCredits + ".");
        }
        this.sessionId = clientId;
        this.peerMaxMessageSize = maxMessageSize;
        this.peerInvocationCredits = invocationCredits;
        this.advertisedIdleTimeoutMillis = idleTimeoutMillis > 0 ? idleTimeoutMillis : -1;
        long interval = effectivePingInterval(pingIntervalMillis, idleTimeoutMillis);
        this.effectivePingIntervalMillis = interval;
        if (pingIntervalMillis != null && idleTimeoutMillis > 0 && interval >= idleTimeoutMillis) {
            log(NexusLogLevel.WARNING, "The ping interval (" + interval + " ms) is not below the server's idle timeout ("
                + idleTimeoutMillis + " ms); the server may time the session out between pings.", null);
        }
        Registration handshakeTimeout;
        synchronized (lock) {
            if (phase != Phase.HANDSHAKING) {
                return;
            }
            phase = Phase.CONNECTED;
            handshakeTimeout = handshakeTimer;
            handshakeTimer = null;
            lastReceivedNanos = timers.nanoTime();
            lock.notifyAll();
        }
        if (handshakeTimeout != null) {
            handshakeTimeout.close();
        }
        schedulePing();
        log(NexusLogLevel.INFORMATION, "Connected as session " + clientId + ".", null);
        ready.complete(null);
    }

    /**
     * The ping interval for a server that advertised {@code advertisedIdleTimeoutMillis}, as C#
     * {@code ClientConfig.GetEffectivePingInterval} (S04): an explicit interval is used as is; otherwise a third of the
     * advertised idle timeout, at least {@link #MIN_DERIVED_PING_INTERVAL_MILLIS}; otherwise
     * {@link #DEFAULT_PING_INTERVAL_MILLIS}.
     *
     * @param configured the configured interval, or null to derive it
     * @param advertisedIdleTimeoutMillis the advertised idle timeout; zero or negative when the server advertised none
     * @return milliseconds
     */
    static long effectivePingInterval(@Nullable Integer configured, long advertisedIdleTimeoutMillis) {
        if (configured != null) {
            return configured;
        }
        if (advertisedIdleTimeoutMillis <= 0) {
            return DEFAULT_PING_INTERVAL_MILLIS;
        }
        return Math.min(Integer.MAX_VALUE, Math.max(MIN_DERIVED_PING_INTERVAL_MILLIS, advertisedIdleTimeoutMillis / 3));
    }

    // ------------------------------------------------------------------ timers

    private void handshakeTimedOut() {
        if (phase() == Phase.HANDSHAKING) {
            log(NexusLogLevel.INFORMATION, "The handshake timed out.", null);
            // No disconnect byte for a handshake timeout (specification section 5.6).
            close(DisconnectReason.TIMEOUT, false);
        }
    }

    private void schedulePing() {
        Registration timer = scheduleTimer(this::pingTick, effectivePingIntervalMillis);
        synchronized (lock) {
            if (phase == Phase.CONNECTED) {
                pingTimer = timer;
                return;
            }
        }
        timer.close();
    }

    /** The ping tick: checks the idle timeout first, then pings (specification section 5.5). */
    private void pingTick() {
        if (phase() != Phase.CONNECTED) {
            return;
        }
        if (timers.nanoTime() - lastReceivedNanos >= timeoutNanos) {
            log(NexusLogLevel.INFORMATION, "Nothing received within the timeout.", null);
            close(DisconnectReason.TIMEOUT, true);
            return;
        }
        if (checkProgress()) {
            return;
        }
        controlSender.requestPing();
        schedulePing();
    }

    /**
     * The progress check (specification section 9): first the outbound transport write in progress, then every probe;
     * the first stall found disconnects with {@code Stalled}. A stalled write closes without the disconnect byte, since
     * the writer thread that would send it is the one that is stuck; closing the transport unblocks it.
     *
     * @return true when the session is closing because of a stall
     */
    private boolean checkProgress() {
        long now = timers.nanoTime();
        String writeStall = findStalledWrite(now);
        if (writeStall != null) {
            log(NexusLogLevel.WARNING, "The session made no progress within "
                + TimeUnit.NANOSECONDS.toMillis(progressTimeoutNanos) + " ms: " + writeStall + ".", null);
            close(DisconnectReason.STALLED, false);
            return true;
        }
        for (ProgressProbe probe : probes) {
            String stall;
            try {
                stall = probe.findStalled(now, progressTimeoutNanos);
            } catch (RuntimeException e) {
                log(NexusLogLevel.ERROR, "A progress probe failed.", e);
                continue;
            }
            if (stall != null) {
                log(NexusLogLevel.WARNING, "The session made no progress within "
                    + TimeUnit.NANOSECONDS.toMillis(progressTimeoutNanos) + " ms: " + stall + ".", null);
                close(DisconnectReason.STALLED, true);
                return true;
            }
        }
        return false;
    }

    /** Describes the outbound transport write in progress when it has run longer than the progress timeout. */
    private @Nullable String findStalledWrite(long now) {
        if (!writing) {
            return null;
        }
        long elapsed = now - writeStartedNanos;
        return elapsed > progressTimeoutNanos
            ? "outbound transport write running for " + TimeUnit.NANOSECONDS.toMillis(elapsed) + " ms"
            : null;
    }

    private Registration scheduleTimer(Runnable task, long delayMillis) {
        AtomicBoolean done = new AtomicBoolean();
        SessionCounters.ACTIVE_TIMERS.incrementAndGet();
        Registration registration = timers.schedule(() -> {
            if (done.compareAndSet(false, true)) {
                SessionCounters.ACTIVE_TIMERS.decrementAndGet();
                task.run();
            }
        }, delayMillis);
        return () -> {
            if (done.compareAndSet(false, true)) {
                SessionCounters.ACTIVE_TIMERS.decrementAndGet();
                registration.close();
            }
        };
    }

    // ------------------------------------------------------------------ closing

    private Phase phase() {
        synchronized (lock) {
            return phase;
        }
    }

    /**
     * Starts closing. With {@code sendByte} the writer thread writes the disconnect byte, waits the disconnect delay
     * and closes; otherwise the transport closes at once.
     */
    void close(DisconnectReason reason, boolean sendByte) {
        boolean writerSends;
        Registration[] timersToCancel;
        synchronized (lock) {
            if (phase.compareTo(Phase.CLOSING) >= 0) {
                return;
            }
            boolean started = phase != Phase.NEW;
            phase = Phase.CLOSING;
            disconnectReason = reason;
            writerSends = sendByte && started && writerThread != null && writerThread.isAlive();
            if (writerSends) {
                terminal = reason;
            }
            timersToCancel = new Registration[] {handshakeTimer, pingTimer};
            handshakeTimer = null;
            pingTimer = null;
            lock.notifyAll();
        }
        for (Registration timer : timersToCancel) {
            if (timer != null) {
                timer.close();
            }
        }
        log(NexusLogLevel.INFORMATION, "Session disconnected with reason: " + reason + ".", null);
        if (!writerSends) {
            finish();
            return;
        }
        // The writer may be stuck in a transport write that never completes, and then never sends the byte. Closing
        // waits at most the progress timeout (plus the disconnect delay) for it, then closes the transport, which
        // unblocks the write. A plain scheduler task, not a session timer: closing cancels the session timers.
        Registration fallback = timers.schedule(this::writerDidNotFinish,
            TimeUnit.NANOSECONDS.toMillis(progressTimeoutNanos) + options.disconnectDelayMillis());
        synchronized (lock) {
            if (phase != Phase.CLOSED) {
                closeFallback = fallback;
                fallback = null;
            }
        }
        if (fallback != null) {
            fallback.close();
        }
    }

    /** The close fallback: the writer did not send the disconnect byte in time. */
    private void writerDidNotFinish() {
        if (finished.get()) {
            return;
        }
        log(NexusLogLevel.WARNING, "The disconnect byte was not written within the progress timeout; closing.", null);
        finish();
    }

    /** The disconnect reason when the session is closing, else {@code fallback}. */
    private DisconnectReason closingReasonOr(DisconnectReason fallback) {
        synchronized (lock) {
            return phase.compareTo(Phase.CLOSING) >= 0 ? disconnectReason : fallback;
        }
    }

    /** Closes the transport and completes everything. Runs once. */
    private void finish() {
        if (!finished.compareAndSet(false, true)) {
            return;
        }
        DisconnectReason reason;
        List<Pending> dropped;
        boolean started;
        Registration fallback;
        synchronized (lock) {
            if (phase.compareTo(Phase.CLOSING) < 0) {
                phase = Phase.CLOSING;
                disconnectReason = DisconnectReason.SOCKET_ERROR;
            }
            reason = disconnectReason;
            started = readerThread != null;
            phase = Phase.CLOSED;
            stopWriter = true;
            fallback = closeFallback;
            closeFallback = null;
            dropped = new ArrayList<>(queue);
            queue.clear();
            SessionCounters.PENDING_WRITES.addAndGet(-dropped.size());
            lock.notifyAll();
        }
        if (fallback != null) {
            fallback.close();
        }
        try {
            transport.close(reason);
        } catch (RuntimeException e) {
            log(NexusLogLevel.DEBUG, "Closing the transport failed.", e);
        }
        fail(dropped, reason);
        if (started) {
            SessionCounters.OPEN_SESSIONS.decrementAndGet();
        }
        ready.completeExceptionally(new SessionClosedException(reason));
        try {
            handler.onClosed(reason);
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "The message handler failed while closing.", e);
        }
        closedInternal.complete(reason);
        NexusExecutors.execute(callbackExecutor, () -> closedPublic.complete(reason));
    }

    // ------------------------------------------------------------------ state

    /**
     * Attaches the handler for messages after the connection completes. Set it before {@link #start()}.
     *
     * @param handler the handler
     */
    public void setMessageHandler(MessageHandler handler) {
        if (handler == null) {
            throw new NullPointerException("handler");
        }
        this.handler = handler;
    }

    /**
     * Whether the connection has completed and not started closing.
     *
     * @return true when connected
     */
    public boolean isConnected() {
        return phase() == Phase.CONNECTED;
    }

    /**
     * Whether the session has started closing.
     *
     * @return true once closing or closed
     */
    public boolean isClosing() {
        return phase().compareTo(Phase.CLOSING) >= 0;
    }

    /**
     * Why the session closed.
     *
     * @return the reason, or {@link DisconnectReason#NONE} while open
     */
    public DisconnectReason disconnectReason() {
        synchronized (lock) {
            return disconnectReason;
        }
    }

    /**
     * The session id the server assigned ({@code ServerGreeting.clientId}).
     *
     * @return the id, or 0 before the connection completes
     */
    public long id() {
        return sessionId;
    }

    /**
     * The session name used in thread names.
     *
     * @return the name
     */
    public String name() {
        return name;
    }

    /**
     * The transport.
     *
     * @return the transport
     */
    public Transport transport() {
        return transport;
    }

    /**
     * Whether the transport encrypts the connection.
     *
     * @return the transport's answer
     */
    public boolean isSecure() {
        return transport.isSecure();
    }

    /**
     * The server certificate of a TLS connection.
     *
     * @return the certificate, or null
     */
    public @Nullable X509Certificate remoteCertificate() {
        return transport.remoteCertificate();
    }

    /**
     * Adds a progress probe, asked on every keepalive tick (specification section 9). A stall it reports
     * disconnects the session with {@link DisconnectReason#STALLED}.
     *
     * @param probe the probe
     */
    public void addProgressProbe(ProgressProbe probe) {
        if (probe == null) {
            throw new NullPointerException("probe");
        }
        probes.add(probe);
    }

    /**
     * The session clock, the one {@link ProgressProbe}s are given.
     *
     * @return nanoseconds, comparable only to other values of this clock
     */
    public long nanoTime() {
        return timers.nanoTime();
    }

    /**
     * The progress timeout.
     *
     * @return milliseconds
     */
    public long progressTimeoutMillis() {
        return TimeUnit.NANOSECONDS.toMillis(progressTimeoutNanos);
    }

    /**
     * The options this session advertised.
     *
     * @return the options
     */
    public SessionOptions options() {
        return options;
    }

    /**
     * The server's advertised {@code maxMessageSize}.
     *
     * @return bytes, or -1 before the connection completes
     */
    public int peerMaxMessageSize() {
        return peerMaxMessageSize;
    }

    /**
     * The largest argument this client may send: the smaller of its own and the server's {@code maxMessageSize}.
     *
     * @return bytes, or -1 before the connection completes
     */
    public int sendMaxMessageSize() {
        int peer = peerMaxMessageSize;
        return peer < 0 ? -1 : Math.min(peer, options.maxMessageSize());
    }

    /**
     * The server's advertised {@code invocationCredits}: how many invocations this client may have queued at it.
     *
     * @return the credits, or -1 before the connection completes
     */
    public int peerInvocationCredits() {
        return peerInvocationCredits;
    }

    /**
     * The idle timeout the server advertised in {@code ServerGreeting} (S04).
     *
     * @return milliseconds, or -1 when the server advertised none
     */
    public long advertisedIdleTimeoutMillis() {
        return advertisedIdleTimeoutMillis;
    }

    /**
     * The ping interval in use: the configured interval when one is set; otherwise a third of the advertised idle
     * timeout, at least 1000 ms; otherwise 10000 ms when the server advertised none.
     *
     * @return milliseconds, or -1 before the connection completes
     */
    public long pingIntervalMillis() {
        return effectivePingIntervalMillis;
    }

    private byte[] clientGreetingBody() {
        WireWriter w = new WireWriter(64 + authenticationToken.length);
        w.writeArrayHeader(6);
        w.writeString(handshake.version());
        w.writeInt(handshake.serverNexusHash());
        w.writeInt(handshake.clientNexusHash());
        w.writeBinary(authenticationToken);
        w.writeInt(options.maxMessageSize());
        w.writeInt(options.invocationCredits());
        return w.toByteArray();
    }

    private void log(NexusLogLevel level, @Nullable String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Session", exception, message == null ? "" : message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the session.
            }
        }
    }

    /** Builds a {@link NexusSession}. */
    public static final class Builder {
        private final Transport transport;
        private final HandshakeDescriptor handshake;
        private String name = "0";
        private byte[] authenticationToken = new byte[0];
        private boolean reconnecting;
        private SessionOptions options = SessionOptions.defaults();
        private int handshakeTimeoutMillis = 15_000;
        private int timeoutMillis = 30_000;
        private int progressTimeoutMillis = 30_000;
        private @Nullable Integer pingIntervalMillis;
        private Executor callbackExecutor = NexusExecutors.shared();
        private SessionTimers timers = SessionTimers.system();
        private @Nullable NexusLogger logger;

        private Builder(Transport transport, HandshakeDescriptor handshake) {
            if (transport == null || handshake == null) {
                throw new NullPointerException(transport == null ? "transport" : "handshake");
            }
            this.transport = transport;
            this.handshake = handshake;
        }

        /**
         * The name in the thread names ({@code nexnet-<name>-reader}).
         *
         * @param name the name
         * @return this builder
         */
        public Builder name(String name) {
            this.name = name;
            return this;
        }

        /**
         * The token sent in the greeting.
         *
         * @param token the token, or null for none; at most 8192 bytes
         * @return this builder
         */
        public Builder authenticationToken(byte @Nullable [] token) {
            byte[] value = token == null ? new byte[0] : token.clone();
            if (value.length > MAX_AUTHENTICATION_TOKEN_LENGTH) {
                throw new IllegalArgumentException("The authentication token is longer than "
                    + MAX_AUTHENTICATION_TOKEN_LENGTH + " bytes.");
            }
            this.authenticationToken = value;
            return this;
        }

        /**
         * Sends {@code ClientGreetingReconnection} instead of {@code ClientGreeting}.
         *
         * @param reconnecting true after a lost connection
         * @return this builder
         */
        public Builder reconnecting(boolean reconnecting) {
            this.reconnecting = reconnecting;
            return this;
        }

        /**
         * The advertised limits and session timings.
         *
         * @param options the options
         * @return this builder
         */
        public Builder options(SessionOptions options) {
            this.options = options;
            return this;
        }

        /**
         * The handshake timeout.
         *
         * @param millis milliseconds
         * @return this builder
         */
        public Builder handshakeTimeoutMillis(int millis) {
            this.handshakeTimeoutMillis = millis;
            return this;
        }

        /**
         * The receive (idle) timeout.
         *
         * @param millis milliseconds
         * @return this builder
         */
        public Builder timeoutMillis(int millis) {
            this.timeoutMillis = millis;
            return this;
        }

        /**
         * The progress timeout: how long a large message may receive no data, or an outbound transport write may run,
         * before the session disconnects with {@code Stalled} (specification section 9).
         *
         * @param millis milliseconds
         * @return this builder
         */
        public Builder progressTimeoutMillis(int millis) {
            this.progressTimeoutMillis = millis;
            return this;
        }

        /**
         * The configured ping interval, used as is. Null (the default) derives it from the server's advertised idle
         * timeout: a third of it, at least 1000 ms, or 10000 ms when the server advertises none.
         *
         * @param millis milliseconds, positive, or null to derive the interval
         * @return this builder
         * @throws IllegalArgumentException when the value is zero or negative
         */
        public Builder pingIntervalMillis(@Nullable Integer millis) {
            if (millis != null && millis <= 0) {
                throw new IllegalArgumentException("pingIntervalMillis must be positive: " + millis);
            }
            this.pingIntervalMillis = millis;
            return this;
        }

        /**
         * The executor that completes {@link NexusSession#closed()}.
         *
         * @param executor the executor
         * @return this builder
         */
        public Builder callbackExecutor(Executor executor) {
            this.callbackExecutor = executor;
            return this;
        }

        /**
         * The clock and timer scheduler.
         *
         * @param timers the timers
         * @return this builder
         */
        public Builder timers(SessionTimers timers) {
            this.timers = timers;
            return this;
        }

        /**
         * The logger.
         *
         * @param logger the logger, or null
         * @return this builder
         */
        public Builder logger(@Nullable NexusLogger logger) {
            this.logger = logger;
            return this;
        }

        /**
         * Builds the session, not yet started.
         *
         * @return the session
         */
        public NexusSession build() {
            return new NexusSession(this);
        }
    }
}
