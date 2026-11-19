package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.invocation.PayloadCodec;
import com.dtronix.nexnet.invocation.SessionBudget;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.InboundFrame;
import com.dtronix.nexnet.session.MessageHandler;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SerialExecutor;
import com.dtronix.nexnet.session.SessionProtocolException;
import com.dtronix.nexnet.session.SessionTimers;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import org.jspecify.annotations.Nullable;

/**
 * The pipe runtime of one session (protocol specification section 7): it allocates pipe ids, reserves receive windows
 * from the session budget, opens pipes in both roles and routes {@code DuplexPipeWrite}, {@code PipeWindowUpdate} and
 * {@code DuplexPipeUpdateState} to them. Attached to an {@link InvocationRuntime} as its next message handler with
 * {@link #attach}; runtime-internal.
 *
 * <p>Frames a pipe sends (data and state) go through one serial send lane, so they keep their order and no caller
 * ever blocks on the session's write queue. Window updates go through the session's control sender.
 */
public final class PipeManager implements MessageHandler {
    /** One wait for session budget for extra credit. */
    private static final class BudgetWaiter {
        final long bytes;
        final CompletableFuture<Boolean> future = new CompletableFuture<>();

        BudgetWaiter(long bytes) {
            this.bytes = bytes;
        }
    }

    private final InvocationRuntime runtime;
    private final NexusSession session;
    private final SessionBudget budget;
    private final PayloadCodec codec;
    private final Executor callbackExecutor;
    private final SessionTimers timers;
    private final PipeOptions options;
    private final @Nullable NexusLogger logger;
    private final SerialExecutor lane = new SerialExecutor(NexusExecutors.shared());

    private final Object lock = new Object();
    private final Map<Integer, PipeImpl> pipes = new HashMap<>();
    private final boolean[] usedIds = new boolean[256];
    private final ArrayDeque<BudgetWaiter> budgetWaiters = new ArrayDeque<>();
    private int currentId;
    private int openPipes;
    private boolean closed;
    private DisconnectReason closeReason = DisconnectReason.NONE;
    private Registration stallTimer = Registration.NONE;
    private Registration budgetTimer = Registration.NONE;

    private PipeManager(InvocationRuntime runtime, Executor callbackExecutor, SessionTimers timers,
                        PipeOptions options, @Nullable NexusLogger logger) {
        this.runtime = runtime;
        this.session = runtime.session();
        this.budget = runtime.budget();
        this.codec = runtime.codec();
        this.callbackExecutor = callbackExecutor;
        this.timers = timers;
        this.options = options;
        this.logger = logger;
    }

    /**
     * Creates the pipe runtime of a session and attaches it to the session's invocation runtime, before the session
     * starts.
     *
     * @param runtime the invocation runtime
     * @param callbackExecutor the executor that completes user-visible futures
     * @param timers the session's timers (the progress timeout)
     * @param options the pipe limits
     * @param logger the logger, or null
     * @return the pipe runtime
     */
    public static PipeManager attach(InvocationRuntime runtime, Executor callbackExecutor, SessionTimers timers,
                                     PipeOptions options, @Nullable NexusLogger logger) {
        if (runtime == null || callbackExecutor == null || timers == null || options == null) {
            throw new NullPointerException();
        }
        PipeManager manager = new PipeManager(runtime, callbackExecutor, timers, options, logger);
        runtime.attachPipes(manager);
        manager.scheduleStallCheck();
        return manager;
    }

    /**
     * The limits.
     *
     * @return the options
     */
    public PipeOptions options() {
        return options;
    }

    /**
     * Pipes of this session holding a receive window; for tests and diagnostics.
     *
     * @return the count
     */
    public int openPipes() {
        synchronized (lock) {
            return openPipes;
        }
    }

    // ------------------------------------------------------------------ opening

    /**
     * Creates a pipe this side opens by passing it to a server method. Its id and receive window are taken now.
     *
     * @return the pipe
     * @throws NexusBusyException when the pipe limit or the session budget is reached
     * @throws IllegalStateException when the session has closed
     */
    public NexusDuplexPipe createPipe() {
        synchronized (lock) {
            if (closed) {
                throw new IllegalStateException("The session has closed.");
            }
            int local = takeWindowAndIdLocked();
            if (local < 0) {
                throw new NexusBusyException("Can't open a pipe: the session's pipe limit or budget is reached.");
            }
            PipeImpl pipe = new PipeImpl(this, 0, local, true, options.pipeWindow());
            // Known by its partial id (its own byte, the server's 0) until the server's Ready.
            pipes.put(local, pipe);
            return pipe;
        }
    }

    /**
     * Creates a typed channel this side opens by passing it to a server method.
     *
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     */
    public <T> NexusDuplexChannel<T> createChannel(Formatter<T> itemFormatter) {
        if (itemFormatter == null) {
            throw new NullPointerException("itemFormatter");
        }
        return new DuplexChannelImpl<>((PipeImpl) createPipe(), itemFormatter, this);
    }

    /**
     * Sends an invocation of a server method with a pipe argument ({@code IgnoreReturn | DuplexPipe}).
     *
     * @param methodId the method id
     * @param pipe a pipe from {@link #createPipe()}, not used before
     * @param arguments the argument writer; receives the pipe's local id
     * @return a future that completes once the invocation is queued for the writer
     */
    public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
        if (pipe == null || arguments == null) {
            throw new NullPointerException(pipe == null ? "pipe" : "arguments");
        }
        if (!(pipe instanceof PipeImpl) || ((PipeImpl) pipe).manager != this) {
            return failed(new IllegalStateException("The pipe was not created by the current session."));
        }
        PipeImpl impl = (PipeImpl) pipe;
        if (!impl.initiating || !impl.markInvoked()) {
            return failed(new IllegalStateException("The pipe was already passed to a method, or closed."));
        }
        byte[] serialized;
        try {
            serialized = codec.writePipeArguments(arguments, impl.localId);
        } catch (RuntimeException e) {
            impl.invocationFailed(e);
            throw e;
        }
        CompletableFuture<Void> sent;
        try {
            sent = runtime.invokeIgnoreReturn(methodId, PipeWire.INVOCATION_FLAGS, serialized);
        } catch (RuntimeException e) {
            impl.invocationFailed(e);
            throw e;
        }
        sent.whenComplete((v, error) -> {
            if (error != null) {
                impl.invocationFailed(unwrap(error));
            }
        });
        return sent;
    }

    /**
     * Sends an invocation of a server method with a channel argument.
     *
     * @param methodId the method id
     * @param channel a channel from {@link #createChannel(Formatter)}, not used before
     * @param arguments the argument writer; receives the channel pipe's local id
     * @param <T> the item type
     * @return a future that completes once the invocation is queued for the writer
     */
    public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                     PipeArgumentWriter arguments) {
        if (channel == null) {
            throw new NullPointerException("channel");
        }
        return invokePipe(methodId, channel.pipe(), arguments);
    }

    /**
     * Registers a pipe the server opened as an argument of a client nexus method, and sends Ready with this side's
     * window. When a limit is reached it sends Refused instead and throws.
     *
     * @param otherId the server's local pipe id read from the arguments (1–255)
     * @return the pipe
     * @throws NexusBusyException when the pipe limit or the session budget is reached
     * @throws IllegalStateException when the session has closed
     */
    public NexusDuplexPipe registerPipe(int otherId) {
        if (otherId < 1 || otherId > 255) {
            throw new IllegalArgumentException("A pipe id is 1 to 255: " + otherId);
        }
        PipeImpl pipe;
        synchronized (lock) {
            if (closed) {
                throw new IllegalStateException("The session has closed.");
            }
            int local = takeWindowAndIdLocked();
            if (local < 0) {
                // Refused carries the opener's partial id: the server's byte, ours 0.
                send(Frame.of(MessageTypes.DUPLEX_PIPE_UPDATE_STATE,
                    PipeWire.stateBody(otherId << 8, PipeWire.REFUSED, 0)));
                throw new NexusBusyException("Refused a pipe: the session's pipe limit or budget is reached.");
            }
            pipe = new PipeImpl(this, local | (otherId << 8), local, false, options.pipeWindow());
            pipes.put(pipe.id(), pipe);
        }
        pipe.sendReady();
        return pipe;
    }

    /**
     * Registers a channel the server opened as an argument of a client nexus method.
     *
     * @param otherId the server's local pipe id read from the arguments (1–255)
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     */
    public <T> NexusDuplexChannel<T> registerChannel(int otherId, Formatter<T> itemFormatter) {
        if (itemFormatter == null) {
            throw new NullPointerException("itemFormatter");
        }
        return new DuplexChannelImpl<>((PipeImpl) registerPipe(otherId), itemFormatter, this);
    }

    /** Takes a pipe slot, a window from the budget and a local id, all or nothing; -1 if any is unavailable. */
    private int takeWindowAndIdLocked() {
        if (openPipes >= options.maxPipes() || !budget.tryReserve(options.pipeWindow())) {
            return -1;
        }
        int id = currentId;
        for (int i = 0; i < 255; i++) {
            if (++id == 256) {
                id = 1;
            }
            if (!usedIds[id]) {
                usedIds[id] = true;
                currentId = id;
                openPipes++;
                PipeCounters.OPEN_PIPES.incrementAndGet();
                return id;
            }
        }
        budget.release(options.pipeWindow());
        return -1;
    }

    // ------------------------------------------------------------------ freeing

    /** Frees a pipe once both sides closed it and the application released it. */
    void tryFree(PipeImpl pipe) {
        if (pipe.tryMarkFreed(false)) {
            free(pipe);
        }
    }

    private void free(PipeImpl pipe) {
        synchronized (lock) {
            int key = pipe.id() != 0 ? pipe.id() : pipe.localId;
            if (pipes.get(key) == pipe) {
                pipes.remove(key);
            }
            if (!closed) {
                usedIds[pipe.localId] = false;
            }
            openPipes--;
        }
        PipeCounters.OPEN_PIPES.decrementAndGet();
        budget.release(pipe.window);
        long extra = pipe.takeExtra();
        if (extra > 0) {
            budget.release(extra);
        }
        pumpBudget();
    }

    // ------------------------------------------------------------------ budget for extra credit

    /** Reserves budget for extra credit, waiting in FIFO order; false once the session closed. */
    CompletableFuture<Boolean> reserveExtra(long bytes) {
        synchronized (lock) {
            if (closed) {
                return CompletableFuture.completedFuture(false);
            }
            if (budgetWaiters.isEmpty() && budget.tryReserve(bytes)) {
                return CompletableFuture.completedFuture(true);
            }
            BudgetWaiter waiter = new BudgetWaiter(bytes);
            budgetWaiters.add(waiter);
            scheduleBudgetRetryLocked();
            return waiter.future;
        }
    }

    void releaseExtra(long bytes) {
        budget.release(bytes);
        pumpBudget();
    }

    private void pumpBudget() {
        List<Runnable> after = new ArrayList<>();
        synchronized (lock) {
            while (!budgetWaiters.isEmpty() && budget.tryReserve(budgetWaiters.peek().bytes)) {
                BudgetWaiter waiter = budgetWaiters.poll();
                after.add(() -> waiter.future.complete(true));
            }
            if (!budgetWaiters.isEmpty()) {
                scheduleBudgetRetryLocked();
            }
        }
        runAll(after);
    }

    private void scheduleBudgetRetryLocked() {
        budgetTimer.close();
        // The invocation runtime releases budget without telling us; look again shortly.
        budgetTimer = timers.schedule(this::pumpBudget, 10);
    }

    // ------------------------------------------------------------------ progress timeout

    private void scheduleStallCheck() {
        long interval = Math.max(1, Math.min(1000, options.progressTimeoutMillis() / 4));
        synchronized (lock) {
            if (!closed) {
                stallTimer = timers.schedule(this::checkStalls, interval);
            }
        }
    }

    private void checkStalls() {
        List<PipeImpl> current;
        synchronized (lock) {
            if (closed) {
                return;
            }
            current = new ArrayList<>(pipes.values());
        }
        long now = timers.nanoTime();
        long timeout = TimeUnit.MILLISECONDS.toNanos(options.progressTimeoutMillis());
        for (PipeImpl pipe : current) {
            String stall = pipe.describeStall(now, timeout);
            if (stall != null) {
                log(NexusLogLevel.WARNING, "Disconnecting: no progress on " + stall + ".", null);
                session.disconnect(DisconnectReason.STALLED);
                return;
            }
        }
        scheduleStallCheck();
    }

    // ------------------------------------------------------------------ receiving

    @Override
    public void onMessage(InboundFrame frame) {
        switch (frame.type()) {
            case MessageTypes.DUPLEX_PIPE_WRITE: {
                PipeImpl pipe = find(frame.pipeId());
                if (pipe == null) {
                    // Pipes stay known while closing, so this id is neither open nor closing.
                    throw PipeWire.protocolError("Data for pipe P" + frame.pipeId() + ", which is not open.");
                }
                if (!pipe.onData(frame.body())) {
                    throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL,
                        "Pipe P" + frame.pipeId() + " received data beyond its credit or after the peer's Complete.");
                }
                break;
            }
            case MessageTypes.PIPE_WINDOW_UPDATE: {
                long grant = PipeWire.parseWindow(frame.body());
                PipeImpl pipe = find(frame.pipeId());
                // A grant can race with this side freeing the pipe; it no longer matters.
                if (pipe != null && !pipe.addSendCredit(grant)) {
                    throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL,
                        "Pipe P" + frame.pipeId() + " was granted more than 2^31 bytes of credit.");
                }
                break;
            }
            case MessageTypes.DUPLEX_PIPE_UPDATE_STATE:
                onState(PipeWire.parseState(frame.body()));
                break;
            default:
                MessageHandler.UNHANDLED.onMessage(frame);
                break;
        }
    }

    private void onState(PipeWire.State message) {
        PipeImpl pipe = find(message.pipeId);
        if (pipe == null) {
            if (message.state != PipeWire.READY) {
                // Neither open, closing nor pending: ignored.
                return;
            }
            int local = message.pipeId & 0xFF;
            synchronized (lock) {
                PipeImpl pending = pipes.get(local);
                if (pending == null || !pending.initiating || (message.pipeId >>> 8) == 0) {
                    throw PipeWire.protocolError("Ready for pipe P" + message.pipeId + ", which is not pending.");
                }
                pipes.remove(local);
                pipes.put(message.pipeId, pending);
                pending.assignId(message.pipeId);
                pipe = pending;
            }
        }
        DisconnectReason violation = pipe.onRemoteState(message.state, message.window);
        if (violation != null) {
            throw new SessionProtocolException(violation, "Pipe P" + message.pipeId + " was granted too much credit.");
        }
        tryFree(pipe);
    }

    private @Nullable PipeImpl find(int id) {
        synchronized (lock) {
            return pipes.get(id);
        }
    }

    @Override
    public void onClosed(DisconnectReason reason) {
        List<PipeImpl> all;
        List<BudgetWaiter> waiters;
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            closeReason = reason;
            all = new ArrayList<>(pipes.values());
            pipes.clear();
            waiters = new ArrayList<>(budgetWaiters);
            budgetWaiters.clear();
            stallTimer.close();
            budgetTimer.close();
        }
        for (BudgetWaiter waiter : waiters) {
            NexusExecutors.execute(callbackExecutor, () -> waiter.future.complete(false));
        }
        for (PipeImpl pipe : all) {
            pipe.onSessionClosed(reason);
            if (pipe.tryMarkFreed(true)) {
                free(pipe);
            }
        }
    }

    // ------------------------------------------------------------------ for the pipes

    DisconnectReason closeReason() {
        synchronized (lock) {
            return closeReason;
        }
    }

    long nanoTime() {
        return timers.nanoTime();
    }

    PayloadCodec codec() {
        return codec;
    }

    /** The largest channel item this side accepts. */
    int maxItemSize() {
        return session.options().maxMessageSize();
    }

    /** The largest channel item the server accepts. */
    int sendMaxItemSize() {
        return session.sendMaxMessageSize();
    }

    /** Queues a frame on the send lane. */
    void send(Frame frame) {
        lane.execute(() -> session.send(frame));
    }

    void requestControl(PipeImpl pipe) {
        session.controlSender().requestControl(pipe);
    }

    /** Runs completions on the callback executor, in order. */
    void runAll(List<Runnable> tasks) {
        if (tasks.isEmpty()) {
            return;
        }
        NexusExecutors.execute(callbackExecutor, () -> {
            for (Runnable task : tasks) {
                task.run();
            }
        });
    }

    <T> CompletableFuture<T> failed(Throwable error) {
        CompletableFuture<T> future = new CompletableFuture<>();
        NexusExecutors.execute(callbackExecutor, () -> future.completeExceptionally(error));
        return future;
    }

    static Throwable unwrap(Throwable error) {
        Throwable current = error;
        while ((current instanceof CompletionException || current instanceof ExecutionException)
            && current.getCause() != null) {
            current = current.getCause();
        }
        return current;
    }

    void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Pipes", exception, message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the session.
            }
        }
    }
}
