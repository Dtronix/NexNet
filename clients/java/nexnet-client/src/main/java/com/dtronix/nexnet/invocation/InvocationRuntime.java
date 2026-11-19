package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.NexusRemoteInvocationException;
import com.dtronix.nexnet.NexusUnauthorizedException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.InboundFrame;
import com.dtronix.nexnet.session.MessageHandler;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SerialExecutor;
import com.dtronix.nexnet.session.SessionProtocolException;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.ClientNexusHooks;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.atomic.AtomicInteger;
import org.jspecify.annotations.Nullable;

/**
 * The invocation runtime of one session, in both directions (protocol specification sections 4.3 to 4.10, 6 and 8).
 *
 * <p>Outbound: the {@link ProxyInvoker} side of generated proxies. Every invocation takes one of the server's
 * invocation credits, waiting in FIFO order when there is none; once it has credit it is queued for the writer from a
 * per-session send lane, so no IO happens on the caller's thread and invocations from one thread keep their order.
 * Results complete the returned futures on the callback executor. A token canceled before the invocation is sent
 * fails the future with a {@link CancellationException} and nothing is sent (D17); after that,
 * {@code InvocationCancellation} is sent, the future completes canceled at once and the late result is consumed.
 * Arguments and results larger than a frame are streamed in chunks that interleave with other frames.
 *
 * <p>Inbound: the {@link MessageHandler} of the session. Invocations of the client nexus are validated against the
 * {@link HandshakeDescriptor}, queued in the normal or the separate pool, and run on the invocation executor when a
 * slot is free; their invocation credit is returned when they are dequeued for execution. Nothing runs before
 * {@link #startDispatching()}.
 *
 * <p>Messages of other types (pipes) go to the {@linkplain #setNextHandler next handler}.
 */
public final class InvocationRuntime implements MessageHandler, ProxyInvoker {
    static final int FLAG_IGNORE_RETURN = 1;
    static final int FLAG_DUPLEX_PIPE = 2;
    static final int STATE_COMPLETED_RESULT = 1;
    static final int STATE_EXCEPTION = 2;
    static final int STATE_UNAUTHORIZED = 3;
    static final int STATE_CANCELED = 4;
    static final int KIND_ARGUMENT = 0;
    static final int KIND_RESULT = 1;
    static final int INNER_INVOCATION = MessageTypes.INVOCATION;
    static final int INNER_RESULT = MessageTypes.INVOCATION_RESULT;
    static final int ABORT_BUSY = 0;
    static final int ABORT_TOO_LARGE = 1;
    static final int ABORT_CANCELLED = 2;
    static final int ABORT_ACK = 3;
    /** Arguments up to this size are never streamed (specification section 6.1). */
    static final int MAX_UNSTREAMED_ARGUMENTS = 65_526;
    static final int MAX_DETAIL_TYPE_LENGTH = 256;
    static final int MAX_DETAIL_MESSAGE_LENGTH = 2048;
    /** Budget reserved per advertised invocation credit for the invocation queue (specification section 9). */
    static final int QUEUE_RESERVATION_PER_CREDIT = 64 * 1024;
    private static final byte[] EMPTY_ARGUMENTS = {(byte) 0x90};

    /** A large message this side is sending. */
    private static final class OutgoingStream {
        volatile boolean aborted;
    }

    /** How a stream this side was sending ended. */
    private enum StreamEnd { SENT, CANCELED, REFUSED, CLOSED }

    /** One pool of execution slots and its queue. */
    private static final class Pool {
        final ArrayDeque<InboundInvocation> queue = new ArrayDeque<>();
        int free;

        Pool(int slots) {
            this.free = slots;
        }
    }

    private final NexusSession session;
    private final HandshakeDescriptor handshake;
    private final ClientNexusBase<?> nexus;
    private final InvocationOptions options;
    private final PayloadCodec codec;
    private final Executor callbackExecutor;
    private final Executor invocationExecutor;
    private final SerialExecutor sendLane;
    private final @Nullable NexusLogger logger;
    private final CreditGate gate = new CreditGate();
    private final SessionBudget budget;
    private final LargeMessageReceiver receiver;
    private final AtomicInteger inboundCredits;
    private final CancellationTokenSource sessionCancellation = new CancellationTokenSource();

    private final Object lock = new Object();
    private final Map<Integer, OutboundCall<?>> outstanding = new HashMap<>();
    private final Set<Integer> reservedIds = new HashSet<>();
    private final Map<Integer, OutgoingStream> outgoingStreams = new HashMap<>();
    private final Map<Integer, InboundInvocation> tracked = new HashMap<>();
    private final Set<InboundInvocation> running = new HashSet<>();
    private final Pool normalPool;
    private final Pool separatePool;
    private int lastId;
    private boolean dispatching;
    private boolean closed;

    private volatile MessageHandler next = MessageHandler.UNHANDLED;

    private InvocationRuntime(Builder b) {
        this.session = b.session;
        this.handshake = b.handshake;
        this.nexus = b.nexus;
        this.options = b.options;
        this.codec = b.codec;
        this.callbackExecutor = b.callbackExecutor;
        this.invocationExecutor = b.invocationExecutor;
        this.logger = b.logger;
        this.sendLane = new SerialExecutor(NexusExecutors.shared());
        this.budget = new SessionBudget(options.sessionBudget());
        int advertised = session.options().invocationCredits();
        // The invocation queue is reserved up front; large messages use what is left.
        budget.tryReserve(Math.min((long) advertised * QUEUE_RESERVATION_PER_CREDIT,
            Math.max(0, options.sessionBudget() - session.options().maxMessageSize())));
        this.inboundCredits = new AtomicInteger(advertised);
        this.normalPool = new Pool(options.maxConcurrentInvocations());
        this.separatePool = new Pool(options.maxSeparatePoolInvocations());
        this.receiver = new LargeMessageReceiver(new ReceiverOwner(), budget, session.options().maxMessageSize());
    }

    /**
     * Starts building the runtime of a session.
     *
     * @param session the session, not yet started
     * @param handshake the generated handshake constants
     * @param nexus the client nexus
     * @return a builder
     */
    public static Builder builder(NexusSession session, HandshakeDescriptor handshake, ClientNexusBase<?> nexus) {
        return new Builder(session, handshake, nexus);
    }

    // ------------------------------------------------------------------ lifecycle

    /**
     * Opens the invocation credits the server advertised. Call once the connection has completed, before handing the
     * runtime to the proxy.
     */
    public void connected() {
        gate.open(session.peerInvocationCredits());
    }

    /**
     * Starts running the invocations the server sends (queued until now). Call after the nexus's
     * {@code onConnected} has completed.
     */
    public void startDispatching() {
        synchronized (lock) {
            dispatching = true;
        }
        dispatch(normalPool);
        dispatch(separatePool);
    }

    /**
     * Sets the handler for the message types this runtime does not handle (pipes). The default ends the session
     * with {@code ProtocolError}.
     *
     * @param handler the handler; it also receives {@link MessageHandler#onClosed}
     */
    public void setNextHandler(MessageHandler handler) {
        if (handler == null) {
            throw new NullPointerException("handler");
        }
        this.next = handler;
    }

    /**
     * The receive budget of the session.
     *
     * @return the budget
     */
    public SessionBudget budget() {
        return budget;
    }

    /**
     * The session.
     *
     * @return the session
     */
    public NexusSession session() {
        return session;
    }

    /**
     * The payload codec.
     *
     * @return the codec
     */
    public PayloadCodec codec() {
        return codec;
    }

    /**
     * Calls waiting for credit, being sent or waiting for their result; for tests and diagnostics.
     *
     * @return the count
     */
    public int pendingCalls() {
        synchronized (lock) {
            return outstanding.size() + gate.waiting();
        }
    }

    /**
     * Invocations of the client nexus queued or running; for tests and diagnostics.
     *
     * @return the count
     */
    public int inboundInvocations() {
        synchronized (lock) {
            return normalPool.queue.size() + separatePool.queue.size() + running.size();
        }
    }

    // ------------------------------------------------------------------ outbound

    @Override
    public <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                           @Nullable CancellationToken cancellationToken) {
        if (result == null) {
            throw new NullPointerException("result");
        }
        return call(methodId, arguments, result, cancellationToken);
    }

    @Override
    public CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                              @Nullable CancellationToken cancellationToken) {
        return call(methodId, arguments, null, cancellationToken);
    }

    @Override
    public void invokeVoid(int methodId, ArgumentWriter arguments) {
        byte[] serialized = codec.writeArguments(arguments);
        int max = session.sendMaxMessageSize();
        if (serialized.length > max) {
            throw new NexusMessageTooLargeException(serialized.length, max);
        }
        invokeIgnoreReturn(methodId, FLAG_IGNORE_RETURN, serialized).whenComplete((v, error) -> {
            if (error != null) {
                log(NexusLogLevel.DEBUG, "Fire-and-forget invocation of method " + methodId + " was not sent.",
                    unwrap(error));
            }
        });
    }

    @Override
    public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
        throw new UnsupportedOperationException("not implemented");
    }

    @Override
    public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                     PipeArgumentWriter arguments) {
        throw new UnsupportedOperationException("not implemented");
    }

    @Override
    public <T> NexusList<T> list(int collectionId, Formatter<T> itemFormatter) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Sends an invocation that gets no result ({@code IgnoreReturn}, with {@code DuplexPipe} for pipe and channel
     * methods). It waits for invocation credit like any invocation; arguments larger than a frame are streamed.
     *
     * @param methodId the method id
     * @param flags the invocation flags; must include {@code IgnoreReturn} (1)
     * @param arguments the serialized arguments (an empty array for none)
     * @return a future that completes on the callback executor once the invocation is queued for the writer in full,
     *     or fails with a {@link CancellationException} when the session ends first
     */
    public CompletableFuture<Void> invokeIgnoreReturn(int methodId, int flags, byte[] arguments) {
        if ((flags & FLAG_IGNORE_RETURN) == 0 || flags < 0 || flags > 0xFF) {
            throw new IllegalArgumentException("flags must include IgnoreReturn: " + flags);
        }
        CompletableFuture<Void> sent = new CompletableFuture<>();
        gate.acquire(new CreditGate.Waiter(
            () -> sendLane.execute(() -> sendIgnoreReturn(methodId, flags, arguments, sent)),
            () -> completeLater(sent, null, new CancellationException("The session closed."))));
        return sent;
    }

    private <R> CompletableFuture<R> call(int methodId, ArgumentWriter arguments, @Nullable Formatter<R> formatter,
                                          @Nullable CancellationToken cancellationToken) {
        OutboundCall<R> call = new OutboundCall<>(this, methodId, formatter);
        InvocationCounters.PENDING_CALLS.incrementAndGet();
        call.whenComplete((r, e) -> InvocationCounters.PENDING_CALLS.decrementAndGet());
        CancellationToken token = cancellationToken == null ? CancellationToken.NONE : cancellationToken;
        if (token.isCancellationRequested()) {
            // Canceled before it is sent: nothing is sent and no id is taken (D17).
            NexusExecutors.execute(callbackExecutor,
                () -> call.completeCanceled("The invocation was canceled before it was sent."));
            return call;
        }
        byte[] serialized;
        try {
            serialized = codec.writeArguments(arguments);
        } catch (RuntimeException e) {
            completeLater(call, null, e);
            return call;
        }
        int max = session.sendMaxMessageSize();
        if (serialized.length > max) {
            completeLater(call, null, new NexusMessageTooLargeException(serialized.length, max));
            return call;
        }
        call.arguments = serialized;
        if (token.canBeCanceled()) {
            // The token's callback runs on the thread that cancels it; the call completes on the callback executor.
            Registration registration = token.register(
                () -> NexusExecutors.execute(callbackExecutor, () -> call.cancel(true)));
            call.whenComplete((r, e) -> registration.close());
        }
        CreditGate.Waiter waiter = new CreditGate.Waiter(
            () -> sendLane.execute(() -> sendCall(call)),
            () -> NexusExecutors.execute(callbackExecutor, () -> call.completeCanceled("The session closed.")));
        call.waiter = waiter;
        gate.acquire(waiter);
        return call;
    }

    /** The cancellation of a call, after its future completed canceled. */
    void onCallCanceled(OutboundCall<?> call) {
        int state;
        int id;
        boolean sendCancellation = false;
        synchronized (call) {
            state = call.state;
            id = call.invocationId;
            if (state == OutboundCall.SENT && !call.cancellationSent) {
                call.cancellationSent = true;
                sendCancellation = true;
            }
        }
        if (state == OutboundCall.WAITING) {
            CreditGate.Waiter waiter = call.waiter;
            // When it was granted meanwhile, the send lane sees the call done and returns the credit.
            if (waiter != null) {
                gate.cancel(waiter);
            }
        } else if (sendCancellation) {
            // Sent: tell the server. The id stays reserved until its result arrives, which is then discarded.
            session.controlSender().requestInvocationCancellation(id);
        }
        // SENDING: the send lane or the stream sees the cancellation when it finishes.
    }

    /** Runs on the send lane once the call has credit. */
    private void sendCall(OutboundCall<?> call) {
        boolean unused;
        synchronized (call) {
            unused = call.isDone();
            if (!unused) {
                call.state = OutboundCall.SENDING;
            }
        }
        if (unused) {
            // Canceled while it waited for credit (the grant raced the cancellation): the credit was not used.
            gate.release(1);
            return;
        }
        int id;
        synchronized (lock) {
            if (closed) {
                id = -1;
            } else {
                id = allocateIdLocked();
                outstanding.put(id, call);
            }
        }
        if (id < 0) {
            NexusExecutors.execute(callbackExecutor, () -> call.completeCanceled("The session closed."));
            return;
        }
        synchronized (call) {
            call.invocationId = id;
        }
        byte[] body = invocationBody(id, call.methodId, 0, call.arguments);
        if (body.length <= Frame.MAX_BODY_LENGTH) {
            CompletableFuture<Void> written = session.send(Frame.of(MessageTypes.INVOCATION, body));
            if (call.markSent()) {
                session.controlSender().requestInvocationCancellation(id);
            }
            written.whenComplete((v, error) -> {
                if (error != null) {
                    onSendFailed(call, id);
                }
            });
            return;
        }
        int max = session.sendMaxMessageSize();
        if (body.length > max) {
            forgetCall(call, id);
            gate.release(1);
            completeLater(call, null, new NexusMessageTooLargeException(body.length, max));
            return;
        }
        stream(id, KIND_ARGUMENT, INNER_INVOCATION, body, call::isCancelled, end -> {
            switch (end) {
                case SENT:
                    if (call.markSent()) {
                        session.controlSender().requestInvocationCancellation(id);
                    }
                    break;
                case CANCELED:
                    // Stopped with MessageAbort(Cancelled): the method never runs and no result follows.
                    forgetCall(call, id);
                    break;
                case REFUSED:
                    // Refused by the server; the abort already failed the call.
                    forgetCall(call, id);
                    break;
                default:
                    break;
            }
        });
    }

    private void onSendFailed(OutboundCall<?> call, int id) {
        // The session ended before the frame was written; onClosed completes the call if this does not.
        forgetCall(call, id);
        NexusExecutors.execute(callbackExecutor, () -> call.completeCanceled("The session closed."));
    }

    /** Releases a call's id, if the call still holds it. */
    private void forgetCall(OutboundCall<?> call, int id) {
        synchronized (lock) {
            if (outstanding.remove(id, call)) {
                reservedIds.remove(id);
            }
        }
    }

    private void sendIgnoreReturn(int methodId, int flags, byte[] arguments, CompletableFuture<Void> sent) {
        // A fire-and-forget id has no meaning, except that a streamed one keys its stream (specification 4.3).
        boolean streamed = arguments.length > MAX_UNSTREAMED_ARGUMENTS;
        int id = 0;
        if (streamed) {
            synchronized (lock) {
                if (closed) {
                    id = -1;
                } else {
                    id = allocateIdLocked();
                }
            }
            if (id < 0) {
                completeLater(sent, null, new CancellationException("The session closed."));
                return;
            }
        }
        byte[] body = invocationBody(id, methodId, flags, arguments);
        if (body.length <= Frame.MAX_BODY_LENGTH) {
            int reserved = id;
            session.send(Frame.of(MessageTypes.INVOCATION, body)).whenComplete((v, error) -> {
                if (streamed) {
                    releaseId(reserved);
                }
                completeLater(sent, null, error == null ? null : new CancellationException("The session closed."));
            });
            return;
        }
        int max = session.sendMaxMessageSize();
        if (body.length > max) {
            releaseId(id);
            gate.release(1);
            completeLater(sent, null, new NexusMessageTooLargeException(body.length, max));
            return;
        }
        int streamId = id;
        stream(id, KIND_ARGUMENT, INNER_INVOCATION, body, () -> false, end -> {
            releaseId(streamId);
            if (end == StreamEnd.SENT) {
                completeLater(sent, null, null);
            } else if (end == StreamEnd.REFUSED) {
                completeLater(sent, null, new NexusBusyException("The server has no room for the argument right now."));
            } else {
                completeLater(sent, null, new CancellationException("The session closed."));
            }
        });
    }

    private void releaseId(int id) {
        synchronized (lock) {
            reservedIds.remove(id);
        }
    }

    private int allocateIdLocked() {
        for (int attempts = 0; attempts <= 0xFFFF; attempts++) {
            lastId = (lastId + 1) & 0xFFFF;
            if (!reservedIds.contains(lastId)) {
                reservedIds.add(lastId);
                return lastId;
            }
        }
        throw new IllegalStateException("All invocation ids are in use.");
    }

    private static byte[] invocationBody(int id, int methodId, int flags, byte[] arguments) {
        WireOut w = new WireOut(16 + arguments.length);
        w.arrayHeader(4);
        w.integer(id);
        w.integer(methodId);
        w.integer(flags);
        if (arguments.length == 0) {
            // Methods without serialized arguments still send an empty array.
            w.raw(EMPTY_ARGUMENTS, 0, 1);
        } else {
            w.raw(arguments, 0, arguments.length);
        }
        return w.toByteArray();
    }

    // ------------------------------------------------------------------ large messages, sending

    /** A condition checked between chunks. */
    private interface Condition {
        boolean test();
    }

    /** Called once when a stream ends. */
    private interface StreamCallback {
        void ended(StreamEnd end);
    }

    /**
     * Streams a body as {@code MessageStart} and {@code MessageChunk} frames (specification section 6.1). The start
     * is queued on the calling thread, so it keeps its place among the invocations; the chunks follow from a pool
     * thread, each queued after the previous one was written, so other frames interleave and an abort stops the
     * stream promptly. A refusal ({@code Busy}/{@code TooLarge}) is answered with {@code Ack} and a cancellation
     * with {@code Cancelled}; no chunk follows either. Both go through the frame queue, after the chunks already
     * queued.
     */
    private void stream(int id, int kind, int innerType, byte[] body, Condition canceled, StreamCallback callback) {
        int key = id | (kind << 16);
        OutgoingStream stream = new OutgoingStream();
        synchronized (lock) {
            outgoingStreams.put(key, stream);
        }
        InvocationCounters.LARGE_MESSAGES.incrementAndGet();
        WireOut start = new WireOut(16);
        start.arrayHeader(4);
        start.integer(id);
        start.integer(kind);
        start.integer(innerType);
        start.integer(body.length);
        CompletableFuture<Void> first = session.send(Frame.of(MessageTypes.MESSAGE_START, start.toByteArray()));
        NexusExecutors.execute(NexusExecutors.shared(), () -> {
            StreamEnd end = StreamEnd.CLOSED;
            try {
                end = pump(id, kind, body, stream, canceled, first);
            } finally {
                synchronized (lock) {
                    outgoingStreams.remove(key, stream);
                }
                InvocationCounters.LARGE_MESSAGES.decrementAndGet();
                try {
                    callback.ended(end);
                } catch (RuntimeException e) {
                    log(NexusLogLevel.ERROR, "Finishing a large message failed.", e);
                }
            }
        });
    }

    private StreamEnd pump(int id, int kind, byte[] body, OutgoingStream stream, Condition canceled,
                           CompletableFuture<Void> first) {
        CompletableFuture<Void> previous = first;
        int offset = 0;
        while (true) {
            try {
                previous.get();
            } catch (ExecutionException e) {
                return StreamEnd.CLOSED;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return StreamEnd.CLOSED;
            }
            if (offset >= body.length) {
                return StreamEnd.SENT;
            }
            if (session.isClosing()) {
                return StreamEnd.CLOSED;
            }
            if (stream.aborted) {
                session.send(abortFrame(id, kind, ABORT_ACK));
                return StreamEnd.REFUSED;
            }
            if (canceled.test()) {
                session.send(abortFrame(id, kind, ABORT_CANCELLED));
                return StreamEnd.CANCELED;
            }
            int count = Math.min(body.length - offset, Frame.MAX_BODY_LENGTH);
            previous = session.send(Frame.of(MessageTypes.MESSAGE_CHUNK, id | (kind << 16), body, offset, count));
            offset += count;
        }
    }

    private static Frame abortFrame(int id, int kind, int reason) {
        WireOut w = new WireOut(8);
        w.arrayHeader(3);
        w.integer(id);
        w.integer(kind);
        w.integer(reason);
        return Frame.of(MessageTypes.MESSAGE_ABORT, w.toByteArray());
    }

    // ------------------------------------------------------------------ receiving

    @Override
    public void onMessage(InboundFrame frame) {
        try {
            switch (frame.type()) {
                case MessageTypes.INVOCATION:
                    onInvocation(frame.body(), 0, false);
                    break;
                case MessageTypes.INVOCATION_RESULT:
                    onResult(frame.body(), 0);
                    break;
                case MessageTypes.INVOCATION_CANCELLATION:
                    onCancellation(frame.body());
                    break;
                case MessageTypes.INVOCATION_CREDIT:
                    onCredit(frame.body());
                    break;
                case MessageTypes.MESSAGE_START:
                    onStart(frame.body());
                    break;
                case MessageTypes.MESSAGE_CHUNK:
                    receiver.onChunk(frame.chunkInvocationId(), frame.chunkKind(), frame.body());
                    break;
                case MessageTypes.MESSAGE_ABORT:
                    onAbort(frame.body());
                    break;
                default:
                    next.onMessage(frame);
                    break;
            }
        } catch (WireIn.WireException e) {
            throw protocolError("Message type " + frame.type() + " does not decode: " + e.getMessage());
        }
    }

    private static SessionProtocolException protocolError(String message) {
        return new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR, message);
    }

    /** An {@code Invocation}, from one frame or reassembled from a large argument. Reader thread. */
    private void onInvocation(byte[] body, long reserved, boolean creditTaken) {
        boolean handedOff = false;
        try {
            WireIn r = new WireIn(body, 0, body.length);
            int count = r.arrayHeader();
            if (count != 4) {
                throw protocolError("Invocation has " + count + " elements.");
            }
            int id = r.readUnsigned(0xFFFF);
            int methodId = r.readUnsigned(0xFFFF);
            int flags = r.readUnsigned(0xFF);
            int argumentsOffset = r.position();
            r.skip();
            int argumentsLength = r.position() - argumentsOffset;
            if (r.remaining() != 0) {
                throw protocolError("Trailing bytes after the invocation arguments.");
            }
            // Checked before the credit is counted (specification section 4.3).
            if (!handshake.acceptsMethod(methodId)) {
                throw protocolError("The server invoked method " + methodId + ", which the client nexus does not have.");
            }
            if (!creditTaken && inboundCredits.decrementAndGet() < 0) {
                throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL,
                    "An invocation received without invocation credit.");
            }
            boolean separate = ClientNexusHooks.separatePool(nexus, methodId);
            InboundInvocation invocation = new InboundInvocation(this, id, methodId, flags, body, argumentsOffset,
                argumentsLength, reserved, separate, sessionCancellation);
            Pool pool = separate ? separatePool : normalPool;
            synchronized (lock) {
                if (closed) {
                    return;
                }
                // Tracked first: the server's cancellation can follow right behind it, before it runs. The server
                // reuses an id only after its result, so a newer invocation replaces an older one.
                if (!invocation.ignoreReturn()) {
                    tracked.put(id, invocation);
                }
                pool.queue.add(invocation);
            }
            handedOff = true;
            InvocationCounters.INBOUND_INVOCATIONS.incrementAndGet();
            dispatch(pool);
        } finally {
            if (!handedOff && reserved > 0) {
                budget.release(reserved);
            }
        }
    }

    /** Runs queued invocations while their pool has free slots. */
    private void dispatch(Pool pool) {
        List<InboundInvocation> start = null;
        synchronized (lock) {
            while (dispatching && !closed && pool.free > 0 && !pool.queue.isEmpty()) {
                pool.free--;
                InboundInvocation invocation = pool.queue.poll();
                running.add(invocation);
                if (start == null) {
                    start = new ArrayList<>();
                }
                start.add(invocation);
            }
        }
        if (start == null) {
            return;
        }
        for (InboundInvocation invocation : start) {
            // Dequeued for execution: the server may queue another (specification section 8).
            returnInvocationCredit();
            NexusExecutors.execute(invocationExecutor, () -> run(invocation));
        }
    }

    private void returnInvocationCredit() {
        inboundCredits.incrementAndGet();
        session.controlSender().returnInvocationCredit(1);
    }

    /** Runs one invocation on the invocation executor. */
    private void run(InboundInvocation invocation) {
        CompletableFuture<Void> done;
        try {
            MsgPackReader reader = codec.reader(invocation.body, invocation.argumentsOffset,
                invocation.argumentsLength);
            done = ClientNexusHooks.dispatch(nexus, invocation.methodId, reader, invocation);
            if (done == null) {
                done = CompletableFuture.completedFuture(null);
            }
        } catch (RuntimeException e) {
            done = new CompletableFuture<>();
            done.completeExceptionally(e);
        }
        done.whenComplete((v, error) -> finish(invocation, error));
    }

    private void finish(InboundInvocation invocation, @Nullable Throwable error) {
        if (invocation.ignoreReturn()) {
            if (error != null) {
                Throwable cause = unwrap(error);
                if (!(cause instanceof CancellationException)) {
                    log(NexusLogLevel.ERROR, "Method " + invocation.methodId + " failed.", cause);
                }
            }
        } else if (invocation.claimResult()) {
            // The dispatch did not set a result: send its outcome.
            CompletableFuture<Void> sent = new CompletableFuture<>();
            sendResultLater(invocation, null, null, error, sent);
        }
        Pool pool = invocation.separatePool ? separatePool : normalPool;
        synchronized (lock) {
            if (running.remove(invocation)) {
                pool.free++;
            }
            tracked.remove(invocation.invocationId, invocation);
        }
        if (invocation.reserved > 0) {
            budget.release(invocation.reserved);
        }
        InvocationCounters.INBOUND_INVOCATIONS.decrementAndGet();
        dispatch(pool);
    }

    /**
     * Sends the result of an invocation from a pool thread, then completes {@code finished}. Called once per
     * invocation that expects a result.
     */
    <R> void sendResultLater(InboundInvocation invocation, @Nullable R value, @Nullable Formatter<R> formatter,
                             @Nullable Throwable error, CompletableFuture<Void> finished) {
        NexusExecutors.execute(NexusExecutors.shared(), () -> {
            try {
                if (error != null) {
                    sendFailure(invocation, error);
                } else {
                    byte[] bytes = null;
                    if (formatter != null) {
                        try {
                            bytes = codec.writeValue(formatter, value);
                        } catch (RuntimeException e) {
                            sendFailure(invocation, e);
                            return;
                        }
                    }
                    sendResult(invocation, STATE_COMPLETED_RESULT, bytes);
                }
            } catch (RuntimeException e) {
                log(NexusLogLevel.ERROR, "Sending the result of method " + invocation.methodId + " failed.", e);
            } finally {
                finished.complete(null);
            }
        });
    }

    private void sendFailure(InboundInvocation invocation, Throwable error) {
        Throwable cause = unwrap(error);
        if (cause instanceof CancellationException) {
            sendResult(invocation, STATE_CANCELED, null);
            return;
        }
        if (cause instanceof NexusMessageTooLargeException || cause instanceof NexusBusyException) {
            // A limit was reached; not a fault in the method.
            log(NexusLogLevel.WARNING, "Method " + invocation.methodId + " failed: " + cause.getMessage(), null);
        } else {
            log(NexusLogLevel.ERROR, "Method " + invocation.methodId + " failed.", cause);
        }
        sendResult(invocation, STATE_EXCEPTION, errorDetail(invocation.methodId, cause));
    }

    /** The detail of a failed invocation (specification section 4.4), or null for none. */
    private byte @Nullable [] errorDetail(int methodId, Throwable exception) {
        NexusRemoteError error = null;
        try {
            error = ClientNexusHooks.invocationException(nexus, exception, methodId);
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "onInvocationException failed.", e);
        }
        Integer code;
        String type;
        String message;
        if (error != null) {
            code = error.code();
            type = null;
            message = error.message();
        } else if (options.remoteExceptionDetail() == RemoteExceptionDetail.TYPE_AND_MESSAGE) {
            code = null;
            type = exception.getClass().getName();
            message = exception.getMessage();
        } else {
            return null;
        }
        WireOut w = new WireOut(64);
        w.arrayHeader(3);
        if (code == null) {
            w.nil();
        } else {
            w.integer(code);
        }
        w.string(truncate(type, MAX_DETAIL_TYPE_LENGTH));
        w.string(truncate(message, MAX_DETAIL_MESSAGE_LENGTH));
        return w.toByteArray();
    }

    /** Truncates to {@code max} UTF-16 chars, never splitting a surrogate pair. */
    static @Nullable String truncate(@Nullable String value, int max) {
        if (value == null || value.length() <= max) {
            return value;
        }
        int cut = max;
        if (Character.isHighSurrogate(value.charAt(cut - 1))) {
            cut--;
        }
        return value.substring(0, cut);
    }

    private void sendResult(InboundInvocation invocation, int state, byte @Nullable [] value) {
        // Untracked before the result goes out: the server may reuse the id as soon as it has the result.
        synchronized (lock) {
            tracked.remove(invocation.invocationId, invocation);
        }
        WireOut w = new WireOut(8 + (value == null ? 0 : value.length));
        w.arrayHeader(value == null ? 2 : 3);
        w.integer(invocation.invocationId);
        w.integer(state);
        if (value != null) {
            w.raw(value, 0, value.length);
        }
        byte[] body = w.toByteArray();
        if (body.length <= Frame.MAX_BODY_LENGTH) {
            session.send(Frame.of(MessageTypes.INVOCATION_RESULT, body));
            return;
        }
        int max = session.sendMaxMessageSize();
        if (body.length > max) {
            NexusMessageTooLargeException tooLarge = new NexusMessageTooLargeException(body.length, max);
            log(NexusLogLevel.WARNING, "Method " + invocation.methodId + " failed: " + tooLarge.getMessage(), null);
            sendResult(invocation, STATE_EXCEPTION, errorDetail(invocation.methodId, tooLarge));
            return;
        }
        stream(invocation.invocationId, KIND_RESULT, INNER_RESULT, body, () -> false, end -> {
        });
    }

    /** An {@code InvocationResult}, from one frame or reassembled. Reader thread. */
    private void onResult(byte[] body, long reserved) {
        boolean handedOff = false;
        try {
            WireIn r = new WireIn(body, 0, body.length);
            int count = r.arrayHeader();
            if (count < 2) {
                throw protocolError("InvocationResult has " + count + " elements.");
            }
            int id = r.readUnsigned(0xFFFF);
            int state = r.readUnsigned(0xFF);
            int valueOffset = r.position();
            if (count >= 3) {
                r.skip();
            }
            int valueLength = r.position() - valueOffset;
            for (int i = 3; i < count; i++) {
                r.skip();
            }
            OutboundCall<?> call;
            synchronized (lock) {
                call = outstanding.remove(id);
                if (call != null) {
                    reservedIds.remove(id);
                }
            }
            if (call == null) {
                throw protocolError("A result for invocation " + id + ", which is not waiting.");
            }
            if (call.isDone()) {
                // Canceled after it was sent: the late result is consumed.
                return;
            }
            boolean hasValue = count >= 3;
            handedOff = true;
            NexusExecutors.execute(callbackExecutor,
                () -> completeCall(call, state, body, valueOffset, valueLength, hasValue, reserved));
        } finally {
            if (!handedOff && reserved > 0) {
                budget.release(reserved);
            }
        }
    }

    private <R> void completeCall(OutboundCall<R> call, int state, byte[] body, int offset, int length,
                                  boolean hasValue, long reserved) {
        R value = null;
        Throwable error = null;
        boolean canceled = false;
        try {
            switch (state) {
                case STATE_COMPLETED_RESULT: {
                    Formatter<R> formatter = call.formatter;
                    if (formatter != null && hasValue) {
                        try {
                            value = codec.readValue(formatter, body, offset, length);
                        } catch (RuntimeException e) {
                            error = new NexusException("The result could not be read: " + e.getMessage(), e);
                        }
                    }
                    break;
                }
                case STATE_EXCEPTION:
                    error = hasValue ? remoteError(body, offset, length)
                        : new NexusRemoteInvocationException(null, null, null);
                    break;
                case STATE_UNAUTHORIZED:
                    error = new NexusUnauthorizedException();
                    break;
                case STATE_CANCELED:
                    canceled = true;
                    break;
                default:
                    error = new NexusException("The server returned the invalid result state " + state + ".");
                    break;
            }
        } finally {
            // Released once the result has been read, before user code sees it.
            if (reserved > 0) {
                budget.release(reserved);
            }
        }
        if (canceled) {
            call.completeCanceled("The server canceled the invocation.");
        } else if (error != null) {
            call.completeExceptionally(error);
        } else {
            call.complete(value);
        }
    }

    /**
     * Reads the detail of an Exception result. A detail of another shape, a code outside int32 or strings that are
     * too long are ignored: the result is an exception without detail, never a protocol error.
     */
    static NexusRemoteInvocationException remoteError(byte[] body, int offset, int length) {
        NexusRemoteInvocationException plain = new NexusRemoteInvocationException(null, null, null);
        try {
            WireIn r = new WireIn(body, offset, length);
            if (r.remaining() == 0 || (r.peek() & 0xF0) != 0x90 || r.arrayHeader() != 3) {
                return plain;
            }
            Integer code = null;
            if (!r.tryNil()) {
                if (!r.isInteger()) {
                    return plain;
                }
                long value = r.readLong();
                if (value < Integer.MIN_VALUE || value > Integer.MAX_VALUE) {
                    return plain;
                }
                code = (int) value;
            }
            String type = null;
            if (!r.tryNil()) {
                if (!r.isString()) {
                    return plain;
                }
                type = r.readString();
                if (type != null && type.length() > MAX_DETAIL_TYPE_LENGTH) {
                    return plain;
                }
            }
            String message = null;
            if (!r.tryNil()) {
                if (!r.isString()) {
                    return plain;
                }
                message = r.readString();
                if (message != null && message.length() > MAX_DETAIL_MESSAGE_LENGTH) {
                    return plain;
                }
            }
            return new NexusRemoteInvocationException(code, type, message);
        } catch (WireIn.WireException e) {
            return plain;
        }
    }

    private void onCancellation(byte[] body) {
        WireIn r = new WireIn(body, 0, body.length);
        if (r.arrayHeader() < 1) {
            throw protocolError("InvocationCancellation has no id.");
        }
        int id = r.readUnsigned(0xFFFF);
        InboundInvocation invocation;
        synchronized (lock) {
            invocation = tracked.get(id);
        }
        // Not tracked: completed already, never received, or fire-and-forget.
        if (invocation != null) {
            // The token's callbacks are user code: never on the reader thread. A queued invocation starts canceled.
            CancellationTokenSource source = invocation.cancellationSource();
            NexusExecutors.execute(invocationExecutor, source::cancel);
        }
    }

    private void onCredit(byte[] body) {
        WireIn r = new WireIn(body, 0, body.length);
        if (r.arrayHeader() < 1) {
            throw protocolError("InvocationCredit has no count.");
        }
        int credits = r.readUnsigned(0xFFFF);
        // Zero, or more than was taken, is a flow-control violation (specification section 8).
        if (credits == 0 || !gate.release(credits)) {
            throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL,
                "The server returned " + credits + " invocation credits it had not been given.");
        }
    }

    private void onStart(byte[] body) {
        WireIn r = new WireIn(body, 0, body.length);
        int count = r.arrayHeader();
        if (count < 4) {
            throw protocolError("MessageStart has " + count + " elements.");
        }
        int id = r.readUnsigned(0xFFFF);
        int kind = r.readUnsigned(0xFF);
        int innerType = r.readUnsigned(0xFF);
        long totalLength = r.readUInt32();
        receiver.onStart(id, kind, innerType, totalLength);
    }

    private void onAbort(byte[] body) {
        WireIn r = new WireIn(body, 0, body.length);
        int count = r.arrayHeader();
        if (count < 3) {
            throw protocolError("MessageAbort has " + count + " elements.");
        }
        int id = r.readUnsigned(0xFFFF);
        int kind = r.readUnsigned(0xFF);
        int reason = r.readUnsigned(0xFF);
        if ((kind != KIND_ARGUMENT && kind != KIND_RESULT) || reason > ABORT_ACK) {
            throw protocolError("MessageAbort with kind " + kind + " and reason " + reason + ".");
        }
        if (reason == ABORT_CANCELLED || reason == ABORT_ACK) {
            // From the sender of a stream this side is receiving.
            receiver.onAbort(id, kind, reason);
            return;
        }
        // From the receiver of a stream this side is sending: stop it (the stream answers with Ack).
        int key = id | (kind << 16);
        OutboundCall<?> call = null;
        synchronized (lock) {
            OutgoingStream stream = outgoingStreams.get(key);
            if (stream != null) {
                stream.aborted = true;
            }
            if (kind == KIND_ARGUMENT) {
                call = outstanding.get(id);
                if (stream == null) {
                    // Already sent in full: no reply comes for a refused argument, so release the id now.
                    outstanding.remove(id);
                    reservedIds.remove(id);
                }
            }
        }
        if (call != null) {
            // A refused argument fails its call, which will not run.
            Throwable failure = reason == ABORT_BUSY
                ? new NexusBusyException("The server has no room for the argument right now.")
                : new NexusMessageTooLargeException(0, session.sendMaxMessageSize());
            completeLater(call, null, failure);
        }
    }

    // ------------------------------------------------------------------ closing

    @Override
    public void onClosed(DisconnectReason reason) {
        List<OutboundCall<?>> calls;
        List<InboundInvocation> queued = new ArrayList<>();
        List<InboundInvocation> active;
        synchronized (lock) {
            closed = true;
            calls = new ArrayList<>(outstanding.values());
            outstanding.clear();
            reservedIds.clear();
            for (OutgoingStream stream : outgoingStreams.values()) {
                stream.aborted = true;
            }
            queued.addAll(normalPool.queue);
            queued.addAll(separatePool.queue);
            normalPool.queue.clear();
            separatePool.queue.clear();
            tracked.clear();
            active = new ArrayList<>(running);
        }
        // Every call still waiting completes as canceled (specification section 4.5).
        gate.close();
        for (OutboundCall<?> call : calls) {
            NexusExecutors.execute(callbackExecutor, () -> call.completeCanceled("The session closed."));
        }
        for (InboundInvocation invocation : queued) {
            InvocationCounters.INBOUND_INVOCATIONS.decrementAndGet();
            if (invocation.reserved > 0) {
                budget.release(invocation.reserved);
            }
        }
        receiver.clear();
        NexusExecutors.execute(invocationExecutor, () -> {
            sessionCancellation.cancel();
            for (InboundInvocation invocation : active) {
                invocation.cancellationSource().cancel();
            }
        });
        try {
            next.onClosed(reason);
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "The next message handler failed while closing.", e);
        }
    }

    // ------------------------------------------------------------------ helpers

    private <T> void completeLater(CompletableFuture<T> future, @Nullable T value, @Nullable Throwable error) {
        NexusExecutors.execute(callbackExecutor, () -> {
            if (error == null) {
                future.complete(value);
            } else if (error instanceof CancellationException && future instanceof OutboundCall) {
                ((OutboundCall<?>) future).completeCanceled(error.getMessage());
            } else {
                future.completeExceptionally(error);
            }
        });
    }

    static Throwable unwrap(Throwable error) {
        Throwable current = error;
        while ((current instanceof CompletionException || current instanceof ExecutionException)
            && current.getCause() != null) {
            current = current.getCause();
        }
        return current;
    }

    private void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Invocation", exception, message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the session.
            }
        }
    }

    /** The receiver's view of this runtime. */
    private final class ReceiverOwner implements LargeMessageReceiver.Owner {
        @Override
        public boolean isCallOutstanding(int invocationId) {
            synchronized (lock) {
                return outstanding.containsKey(invocationId);
            }
        }

        @Override
        public boolean tryTakeInvocationCredit() {
            return inboundCredits.decrementAndGet() >= 0;
        }

        @Override
        public void returnInvocationCredit() {
            InvocationRuntime.this.returnInvocationCredit();
        }

        @Override
        public void sendAbort(int invocationId, int kind, int reason) {
            session.controlSender().requestAbort(invocationId, kind, reason);
        }

        @Override
        public void onComplete(int kind, byte[] body, long reserved) {
            if (kind == KIND_ARGUMENT) {
                onInvocation(body, reserved, true);
            } else {
                onResult(body, reserved);
            }
        }
    }

    /** Builds an {@link InvocationRuntime}. */
    public static final class Builder {
        private final NexusSession session;
        private final HandshakeDescriptor handshake;
        private final ClientNexusBase<?> nexus;
        private InvocationOptions options = InvocationOptions.defaults();
        private PayloadCodec codec = PayloadCodec.MSGPACK;
        private Executor callbackExecutor = NexusExecutors.shared();
        private Executor invocationExecutor = NexusExecutors.shared();
        private @Nullable NexusLogger logger;

        private Builder(NexusSession session, HandshakeDescriptor handshake, ClientNexusBase<?> nexus) {
            if (session == null || handshake == null || nexus == null) {
                throw new NullPointerException("An argument is null.");
            }
            this.session = session;
            this.handshake = handshake;
            this.nexus = nexus;
        }

        /**
         * The invocation limits.
         *
         * @param options the options
         * @return this builder
         */
        public Builder options(InvocationOptions options) {
            this.options = options;
            return this;
        }

        /**
         * The payload codec; {@link PayloadCodec#MSGPACK} by default.
         *
         * @param codec the codec
         * @return this builder
         */
        public Builder codec(PayloadCodec codec) {
            this.codec = codec;
            return this;
        }

        /**
         * The executor that completes the futures of calls.
         *
         * @param executor the executor
         * @return this builder
         */
        public Builder callbackExecutor(Executor executor) {
            this.callbackExecutor = executor;
            return this;
        }

        /**
         * The executor that runs invocations of the client nexus.
         *
         * @param executor the executor
         * @return this builder
         */
        public Builder invocationExecutor(Executor executor) {
            this.invocationExecutor = executor;
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
         * Builds the runtime. Attach it with {@code session.setMessageHandler(runtime)} before starting the session.
         *
         * @return the runtime
         */
        public InvocationRuntime build() {
            return new InvocationRuntime(this);
        }
    }
}
