package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.collections.NexusListChange;
import com.dtronix.nexnet.collections.NexusListClient;
import com.dtronix.nexnet.diagnostics.Diagnostics;
import com.dtronix.nexnet.diagnostics.NexusBudgetSnapshot;
import com.dtronix.nexnet.diagnostics.NexusSessionSnapshot;
import com.dtronix.nexnet.invocation.NexusRemoteError;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.reconnect.ReconnectController;
import com.dtronix.nexnet.reconnect.ReconnectListener;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptCall;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptHost;
import com.dtronix.nexnet.testnexus.scaffolding.TypedCodec;
import java.security.cert.CertificateEncodingException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * A client of {@code client.create} (control protocol §6.4): the Java client with a scriptable client nexus whose
 * calls run the scripts of §6.6.
 */
final class ClientEntry extends Driven implements ScriptHost {
    /** The message of the exception a {@code list.watch} listener with {@code throws} raises. */
    static final String THROWING_WATCH_MESSAGE = "A scripted list change handler threw (list.watch throws).";

    private volatile NexusClient<?> client;
    final FakeTimers fakeTime;
    private final CancellationTokenSource lifetime = new CancellationTokenSource();
    private final Map<String, Slot> slots = new ConcurrentHashMap<>();
    private final Map<String, CompletableFuture<Void>> signals = new ConcurrentHashMap<>();
    private final Map<String, Registration> watches = new ConcurrentHashMap<>();
    /** {@code list.throttle} slow consumers, by collection name (§6.10). */
    private final Map<String, ListThrottle> throttles = new ConcurrentHashMap<>();
    final List<PipeEntry> pipes = new CopyOnWriteArrayList<>();
    /** Pipes {@code pipe.hold} steps published, by name, and the {@code pipe.accept} requests waiting for one. */
    private final Map<String, HeldBox> held = new ConcurrentHashMap<>();
    private volatile NexusSession lastSession;
    /** The reason of the last failed connection attempt; cleared when a session is established. */
    private volatile String lastFailure;
    private volatile boolean disposed;
    /** The error the onInvocationException hook returns ({@code ClientSpec.onInvocationException}), or null. */
    private final NexusRemoteError invocationError;
    /** The client's metrics ({@code ClientSpec.metrics}), or null when it records none. */
    private volatile MetricsRecorder metrics;
    /** The states the client reported, in order, since creation or the last reset (§6.4 {@code client.stateHistory}). */
    private final List<String> stateHistory = new ArrayList<>();
    /** The reconnection attempts the client started since creation or the last reset; guarded by {@link #stateHistory}. */
    private int reconnectAttempts;

    /** The held pipes published under one name (§6.9 {@code pipe.hold}). */
    private static final class HeldBox {
        final java.util.ArrayDeque<PipeEntry> ready = new java.util.ArrayDeque<>();
        final java.util.ArrayDeque<CompletableFuture<PipeEntry>> waiters = new java.util.ArrayDeque<>();
    }

    /** The script of a slot (§6.6). */
    private static final class Slot {
        final List<Object> steps;
        final Long times;
        final AtomicInteger calls = new AtomicInteger();

        Slot(List<Object> steps, Long times) {
            this.steps = steps;
            this.times = times;
        }
    }

    private ClientEntry(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export, FakeTimers fakeTime,
                        NexusRemoteError invocationError) {
        super(host, scope, handle, export);
        this.fakeTime = fakeTime;
        this.invocationError = invocationError;
    }

    static ClientEntry create(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export, Args spec) {
        String level = spec.optStr("logLevel");
        ClientLogger logger = level == null ? null
            : new ClientLogger(host, scope.id, handle, Names.parse(NexusLogLevel.class, level));
        SpecMapper.Setup setup = SpecMapper.map(spec, logger, null);
        ClientEntry entry = new ClientEntry(host, scope, handle, export, setup.fakeTime, setup.invocationError);
        NexusClient<?> client = newClient(export, setup, entry);
        entry.client = client;
        // Tracked from creation, so every session of the client is counted.
        entry.metrics = MetricsRecorder.create(spec.optStr("metrics"), client);
        client.addStateListener(entry::onState);
        ReconnectController reconnection = client.reconnection();
        if (reconnection != null) {
            reconnection.addListener(new ReconnectListener() {
                @Override
                public void onAttempt(int attempt) {
                    synchronized (entry.stateHistory) {
                        entry.reconnectAttempts++;
                    }
                }
            });
        }
        return entry;
    }

    private static <P> NexusClient<P> newClient(ScaffoldingExport<P> export, SpecMapper.Setup setup, ScriptHost host) {
        return NexusClient.create(setup.config, export.handshake(), export.createNexus(host), export.proxyFactory(),
            setup.connector, setup.timers, setup.sessionOptions);
    }

    private void onState(ConnectionState state) {
        if (state == ConnectionState.CONNECTED) {
            NexusSession session = client.session();
            if (session != null) {
                lastSession = session;
                lastFailure = null;
            }
        }
        synchronized (stateHistory) {
            stateHistory.add(Names.pascal(state));
        }
        host.event(scope.id, handle, "client.state", Protocol.map("state", Names.pascal(state)));
    }

    /**
     * The states the client reported and the reconnection attempts it started (§6.4 {@code client.stateHistory}),
     * since creation or the last reset.
     *
     * @param reset clears the history after reading it
     * @return {@code {states, reconnectAttempts}}
     */
    Map<String, Object> stateHistory(boolean reset) {
        synchronized (stateHistory) {
            Map<String, Object> result = Protocol.map("states", new ArrayList<Object>(stateHistory),
                "reconnectAttempts", reconnectAttempts);
            if (reset) {
                stateHistory.clear();
                reconnectAttempts = 0;
            }
            return result;
        }
    }

    @Override
    NexusClient<?> client() {
        return client;
    }

    @Override
    Object proxy() {
        return client.proxy();
    }

    @Override
    NexusSession lastSession() {
        return lastSession;
    }

    @Override
    String lastFailure() {
        return lastFailure;
    }

    @Override
    Object pipeArgument(String pipeHandle, ScaffoldingExport.Method method) {
        PipeEntry pipe = scope.get(pipeHandle, PipeEntry.class);
        if (method.pipeKind() == ScaffoldingExport.PipeKind.CHANNEL) {
            return pipe.channel(method.channelItem());
        }
        return pipe.pipe;
    }

    boolean isDisposed() {
        return disposed;
    }

    // ------------------------------------------------------------------ lifecycle

    CompletableFuture<Map<String, Object>> connect() {
        return client.tryConnect().thenApply(result -> {
            onAttempt(result);
            if (!result.isSuccess()) {
                throw new CompletionException(connectError(result));
            }
            return Protocol.empty();
        });
    }

    CompletableFuture<Map<String, Object>> tryConnect() {
        return client.tryConnect().thenApply(result -> {
            onAttempt(result);
            return Protocol.map("ok", result.isSuccess());
        });
    }

    /** Remembers why an attempt failed (the session of a refused handshake is never the client's). */
    private void onAttempt(ConnectionResult result) {
        if (!result.isSuccess()) {
            lastFailure = Names.pascal(result.disconnectReason());
        }
    }

    /** The exception {@code NexusClient.connect()} fails with for an unsuccessful result. */
    private static Throwable connectError(ConnectionResult result) {
        if (result.exception() != null) {
            return result.exception();
        }
        if (result.disconnectReason() == DisconnectReason.AUTHENTICATION) {
            return new NexusTransportException(TransportError.AUTHENTICATION_ERROR, "Authentication failed.", null);
        }
        return new NexusException("The server closed the connection during the handshake: "
            + result.disconnectReason() + ".");
    }

    CompletableFuture<Map<String, Object>> disconnect() {
        return client.disconnect().thenCompose(v -> awaitState("Disconnected"));
    }

    /** Releases the client and everything it owns; blocks while the client closes (run it off the dispatcher). */
    void dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        lifetime.cancel();
        for (Registration watch : watches.values()) {
            watch.close();
        }
        watches.clear();
        for (ListThrottle throttle : throttles.values()) {
            throttle.stop();
        }
        throttles.clear();
        for (PipeEntry pipe : pipes) {
            pipe.dispose();
        }
        try {
            client.close();
        } catch (RuntimeException ignored) {
            // Closing anyway.
        }
        MetricsRecorder recorded = metrics;
        if (recorded != null) {
            recorded.close();
        }
    }

    /**
     * The certificate the server presented on the current connection, as base64 DER (§6.4 {@code client.remoteCertificate}).
     *
     * @return the base64 DER, or null when the client is not connected or its transport has no TLS
     */
    String remoteCertificate() {
        NexusSession session = client.session();
        if (client.state() != ConnectionState.CONNECTED || session == null) {
            return null;
        }
        X509Certificate certificate = session.remoteCertificate();
        if (certificate == null) {
            return null;
        }
        try {
            return Base64.getEncoder().encodeToString(certificate.getEncoded());
        } catch (CertificateEncodingException e) {
            throw new HostError(HostError.INVALID_OPERATION, "The server certificate cannot be encoded: " + e.getMessage());
        }
    }

    /**
     * Whether the client's transport encrypts the connection (§6.4 {@code client.isSecure}).
     *
     * @return {@link NexusClient#isSecure()}
     */
    boolean isSecure() {
        return client.isSecure();
    }

    /**
     * The server's address and port on the current connection (§6.4 {@code client.remoteEndpoint}).
     *
     * @return {@code {address, port}}: both null when the client is not connected; the port is null when the transport
     *     reports none
     */
    Map<String, Object> remoteEndpoint() {
        NexusSession session = client.session();
        if (client.state() != ConnectionState.CONNECTED || session == null) {
            return Protocol.map("address", null, "port", null);
        }
        int port = session.transport().remotePort();
        return Protocol.map("address", session.transport().remoteAddress(), "port", port < 0 ? null : port);
    }

    /**
     * The receive budget of the current session (§6.4 {@code client.budget}).
     *
     * @return {@code {budget: {limit, reserved}}}, or {@code {budget: null}} when the client has no session
     */
    Map<String, Object> budget() {
        NexusSessionSnapshot snapshot = Diagnostics.snapshot(client);
        NexusBudgetSnapshot budget = snapshot == null ? null : snapshot.budget();
        return Protocol.map("budget", budget == null ? null : Protocol.map("limit", budget.limit(), "reserved", budget.reserved()));
    }

    /**
     * The outbound invocation ids the current session holds (§6.4 {@code client.outstanding}), including a call
     * canceled after send until its reply releases the id.
     *
     * @return {@code {invocations}}: {@link Diagnostics#reservedInvocationIds}, 0 when the client has no session
     */
    /**
     * The snapshot of the current session (§6.4 {@code client.snapshot}).
     *
     * @return {@code {snapshot}}: null when the client has no session that completed the handshake
     */
    Map<String, Object> snapshot() {
        return Protocol.map("snapshot", SnapshotJson.of(Diagnostics.snapshot(client)));
    }

    /**
     * The client's metrics (§6.4 {@code client.metrics}).
     *
     * @return {@code {events, observed}}
     * @throws HostError InvalidOperation when the client was created without {@code spec.metrics}
     */
    Map<String, Object> metrics() {
        MetricsRecorder recorded = metrics;
        if (recorded == null) {
            throw new HostError(HostError.INVALID_OPERATION, "Create the client with ClientSpec.metrics to read its metrics.");
        }
        return recorded.read();
    }

    Map<String, Object> outstanding() {
        return Protocol.map("invocations", (long) Diagnostics.reservedInvocationIds(client));
    }

    Map<String, Object> advanceTime(long millis) {
        if (fakeTime == null) {
            throw new HostError(HostError.INVALID_OPERATION,
                "Create the client with ClientSpec.fakeTime to move its clock.");
        }
        fakeTime.advance(millis);
        return Protocol.empty();
    }

    // ------------------------------------------------------------------ scripts (§6.6)

    Map<String, Object> setScript(Args args) {
        String slot = requireSlot(args.str("slot"));
        List<Object> steps = new ArrayList<>(args.list("steps"));
        for (Object step : steps) {
            if (!(step instanceof Map)) {
                throw HostError.badRequest("A script step must be an object.");
            }
        }
        Long times = args.optInteger("times");
        if (times != null && times < 1) {
            throw HostError.badRequest("times must be at least 1.");
        }
        slots.put(slot, new Slot(Collections.unmodifiableList(steps), times));
        return Protocol.empty();
    }

    Map<String, Object> clearScript(Args args) {
        slots.put(requireSlot(args.str("slot")), new Slot(Collections.emptyList(), null));
        return Protocol.empty();
    }

    private String requireSlot(String slot) {
        if (!export.slots().contains(slot)) {
            throw HostError.badRequest("The client nexus of " + export + " has no slot '" + slot + "'.");
        }
        return slot;
    }

    Map<String, Object> signal(String name) {
        signal$(name).complete(null);
        return Protocol.empty();
    }

    private CompletableFuture<Void> signal$(String name) {
        return signals.computeIfAbsent(name, k -> new CompletableFuture<>());
    }

    @Override
    public NexusRemoteError onInvocationException(Throwable exception, int methodId) {
        return invocationError;
    }

    @Override
    public CompletableFuture<Object> call(ScriptCall call) {
        host.event(scope.id, handle, "script.call",
            Protocol.map("slot", call.slot(), "args", new ArrayList<>(call.arguments())));
        Slot slot = slots.get(call.slot());
        List<Object> steps = slot == null ? Collections.emptyList() : slot.steps;
        if (slot != null && slot.times != null && slot.calls.incrementAndGet() > slot.times) {
            steps = Collections.emptyList();
        }
        CancellationToken token = call.cancellationToken().canBeCanceled() ? call.cancellationToken()
            : lifetime.token();
        try {
            return runSteps(steps, 0, call, token);
        } catch (RuntimeException e) {
            return Async.failed(e);
        }
    }

    private CompletableFuture<Object> runSteps(List<Object> steps, int start, ScriptCall call, CancellationToken token) {
        for (int i = start; i < steps.size(); i++) {
            @SuppressWarnings("unchecked")
            Args step = new Args((Map<String, Object>) steps.get(i));
            String kind = step.str("step");
            CompletableFuture<?> wait;
            switch (kind) {
                case "return":
                    return CompletableFuture.completedFuture(step.value("value"));
                case "throw":
                    return Async.failed(Errors.nativeException(step.optStr("kind"), step.optStr("message")));
                case "forward":
                    return forward(call, token);
                case "delay":
                    wait = Async.delay(host.timer, step.integer("ms"), token);
                    break;
                case "awaitSignal":
                    wait = Async.withToken(signal$(step.str("name")), token);
                    break;
                case "awaitCancel":
                    wait = Async.whenCanceled(token);
                    break;
                case "invoke": {
                    String name = step.obj("method").str("name");
                    wait = export.invokeUnchecked(client.proxy(), name, step.list("args"), null, CancellationToken.NONE);
                    break;
                }
                case "named":
                    wait = "pipe.hold".equals(step.str("handler"))
                        ? hold(step.optObj("options"), call)
                        : NamedHandlers.run(host.timer, step.str("handler"), step.optObj("options"), call, token);
                    break;
                default:
                    return Async.failed(HostError.notSupported("Unknown script step '" + kind + "'."));
            }
            if (wait.isDone() && !wait.isCompletedExceptionally()) {
                continue;
            }
            int next = i + 1;
            return wait.thenCompose(v -> runSteps(steps, next, call, token));
        }
        return CompletableFuture.completedFuture(null);
    }

    private CompletableFuture<Object> forward(ScriptCall call, CancellationToken token) {
        long callId = host.nextCallId();
        CompletableFuture<Object> result = new CompletableFuture<>();
        host.forwards.put(callId, result);
        host.event(scope.id, handle, "script.forward",
            Protocol.map("callId", callId, "slot", call.slot(), "args", new ArrayList<>(call.arguments())));
        Registration registration = token.register(() -> {
            if (!result.isDone()) {
                host.event(scope.id, handle, "script.forwardCancel", Protocol.map("callId", callId));
                result.completeExceptionally(new CancellationException("The forwarded call was canceled."));
            }
        });
        result.whenComplete((v, e) -> {
            registration.close();
            host.forwards.remove(callId);
        });
        return result;
    }

    // ------------------------------------------------------------------ pipes (§6.7)

    PipeEntry createPipe() {
        PipeEntry pipe = new PipeEntry(this, host.newHandle("p"), client.createPipe());
        pipes.add(pipe);
        scope.put(pipe.handle, pipe);
        return pipe;
    }

    /**
     * {@code pipe.hold} (§6.9): registers the pipe the server passed to the method as a pipe handle of this client,
     * publishes it for {@code pipe.accept} and completes when the handle is disposed (or the client is). It ignores the
     * call's cancellation, so the runner still drives the pipe after a disconnect.
     */
    private CompletableFuture<?> hold(Args options, ScriptCall call) {
        String name = options == null ? null : options.optStr("name");
        if (name == null || name.isEmpty()) {
            throw HostError.badRequest("pipe.hold needs the option 'name'.");
        }
        if (!(call.pipe() instanceof NexusDuplexPipe)) {
            throw new IllegalStateException("The handler 'pipe.hold' needs a method that takes a pipe.");
        }
        PipeEntry pipe = new PipeEntry(this, host.newHandle("p"), (NexusDuplexPipe) call.pipe(), true);
        pipes.add(pipe);
        scope.put(pipe.handle, pipe);
        HeldBox box = held.computeIfAbsent(name, k -> new HeldBox());
        synchronized (box) {
            boolean taken = false;
            while (!taken && !box.waiters.isEmpty()) {
                // A waiter whose request timed out is already complete: skip it.
                taken = box.waiters.poll().complete(pipe);
            }
            if (!taken) {
                box.ready.add(pipe);
            }
        }
        return CompletableFuture.anyOf(pipe.released, Async.whenCanceled(lifetime.token()));
    }

    /** {@code pipe.accept} (§6.7): the next pipe published under {@code name}, waiting for one. */
    CompletableFuture<Map<String, Object>> acceptPipe(String name) {
        HeldBox box = held.computeIfAbsent(name, k -> new HeldBox());
        CompletableFuture<PipeEntry> next = new CompletableFuture<>();
        synchronized (box) {
            PipeEntry ready = box.ready.poll();
            if (ready != null) {
                next.complete(ready);
            } else {
                box.waiters.add(next);
            }
        }
        return next.thenApply(pipe -> Protocol.map("pipe", pipe.handle));
    }

    // ------------------------------------------------------------------ collections (§6.10)

    @SuppressWarnings("unchecked")
    private NexusList<Object> list(Args args) {
        return (NexusList<Object>) export.listUnchecked(client.proxy(), args.obj("collection").str("name"));
    }

    private TypedCodec<Object> itemCodec(Args args) {
        String name = args.obj("collection").str("name");
        ScaffoldingExport.Collection collection = export.collection(name);
        if (collection == null) {
            throw HostError.badRequest("Unknown collection '" + name + "' of " + export + ".");
        }
        return PipeEntry.codec(collection.item());
    }

    CompletableFuture<Map<String, Object>> listEnable(Args args, CancellationToken token) {
        NexusList<Object> list = list(args);
        CompletableFuture<Boolean> enabled = list.enable().thenCompose(ok -> ok
            ? list.ready().thenApply(v -> true)
            : CompletableFuture.completedFuture(false));
        return Async.withToken(enabled, token).thenApply(ok -> Protocol.map("ok", ok));
    }

    CompletableFuture<Map<String, Object>> listDisable(Args args) {
        return list(args).disable().thenApply(v -> Protocol.empty());
    }

    CompletableFuture<Map<String, Object>> listOp(Args args) {
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        CompletableFuture<Boolean> result;
        switch (args.str("op")) {
            case "add":
                result = list.addAsync(codec.fromJson(args.value("item")));
                break;
            case "insert":
                result = list.insertAsync(args.int32("index"), codec.fromJson(args.value("item")));
                break;
            case "remove":
                result = list.removeAsync(codec.fromJson(args.value("item")));
                break;
            case "removeAt":
                result = list.removeAtAsync(args.int32("index"));
                break;
            case "replace":
                result = list.replaceAsync(args.int32("index"), codec.fromJson(args.value("item")));
                break;
            case "move":
                result = list.moveAsync(args.int32("from"), args.int32("to"));
                break;
            case "clear":
                result = list.clearAsync();
                break;
            default:
                throw HostError.badRequest("Unknown list op '" + args.str("op") + "'.");
        }
        if (args.bool("count")) {
            // Read the moment the op completes: every change the server sent before its answer is applied by then (§6.10).
            return result.thenApply(ok -> Protocol.map("ok", ok, "count", list.size()));
        }
        return result.thenApply(ok -> Protocol.map("ok", ok));
    }

    Map<String, Object> listSnapshot(Args args) {
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        List<Object> items = new ArrayList<>();
        for (Object item : list.toArray()) {
            items.add(codec.toJson(item));
        }
        return Protocol.map("items", items);
    }

    /** {@code list.state} (§6.10): the mode, state and disabled signal of the collection. */
    Map<String, Object> listState(Args args) {
        NexusList<Object> list = list(args);
        if (!(list instanceof NexusListClient)) {
            throw new HostError(HostError.INVALID_OPERATION, "The collection is not a runtime list; its mode is unknown.");
        }
        // The Java list has no read-only flag (C# IsReadOnly): its mode tells whether the client can change it.
        return Protocol.map(
            "mode", Names.pascal(((NexusListClient<?>) list).mode()),
            "state", Names.pascal(list.state()),
            "disabled", list.disabled().isDone());
    }

    /** {@code list.throttle} (§6.10): installs, replaces or removes the list's slow consumer. */
    Map<String, Object> listThrottle(Args args) {
        NexusListClient<?> list = runtimeList(args);
        String name = args.obj("collection").str("name");
        int every = optInt(args, "every", 1, 1);
        int delayMs = optInt(args, "delayMs", 0, 0);
        int firstDelayMs = optInt(args, "firstDelayMs", 0, 0);
        boolean pause = args.bool("pause");
        // A new throttle replaces the last one, and releases the message it holds.
        ListThrottle previous = throttles.remove(name);
        if (previous != null) {
            previous.stop();
        }
        if (!ListThrottle.isActive(delayMs, firstDelayMs, pause)) {
            list.setProcessTestHook(null);
            return Protocol.empty();
        }
        ListThrottle throttle = new ListThrottle(host.timer, every, delayMs, firstDelayMs, pause);
        throttles.put(name, throttle);
        list.setProcessTestHook(throttle);
        return Protocol.empty();
    }

    /** {@code list.throttleState} (§6.10): what the list's slow consumer has seen, and whether it holds a message. */
    Map<String, Object> listThrottleState(Args args) {
        runtimeList(args);
        ListThrottle throttle = throttles.get(args.obj("collection").str("name"));
        return Protocol.map(
            "seen", throttle == null ? 0L : (long) throttle.seen(),
            "holding", throttle != null && throttle.holding());
    }

    private NexusListClient<?> runtimeList(Args args) {
        NexusList<Object> list = list(args);
        if (!(list instanceof NexusListClient)) {
            throw new HostError(HostError.INVALID_OPERATION, "The collection is not a runtime list; it has no process test hook.");
        }
        return (NexusListClient<?>) list;
    }

    private static int optInt(Args args, String name, int min, int fallback) {
        Long value = args.optInteger(name);
        if (value == null) {
            return fallback;
        }
        if (value < min || value > Integer.MAX_VALUE) {
            throw HostError.badRequest("The argument '" + name + "' is out of range.");
        }
        return value.intValue();
    }

    Map<String, Object> listWatch(Args args) {
        String watch = args.str("watch");
        NexusList<Object> list = list(args);
        TypedCodec<Object> codec = itemCodec(args);
        if (watches.containsKey(watch)) {
            throw HostError.badRequest("The watch '" + watch + "' is in use.");
        }
        boolean throwing = args.bool("throws");
        Registration registration = list.addChangeListener(change -> {
            host.event(scope.id, handle, "list.changed", changeJson(watch, change, codec));
            if (throwing) {
                // list.watch throws: the runtime must survive a listener that throws (control protocol §6.10).
                throw new IllegalStateException(THROWING_WATCH_MESSAGE);
            }
        });
        watches.put(watch, registration);
        return Protocol.empty();
    }

    Map<String, Object> listUnwatch(Args args) {
        String watch = args.str("watch");
        Registration registration = watches.remove(watch);
        if (registration == null) {
            throw HostError.unknownHandle(watch);
        }
        registration.close();
        return Protocol.empty();
    }

    private static Map<String, Object> changeJson(String watch, NexusListChange<Object> change, TypedCodec<Object> codec) {
        Map<String, Object> data = Protocol.map("watch", watch, "action", Names.pascal(change.action()));
        switch (change.action()) {
            case ADD:
            case REPLACE:
            case MOVE:
                if (change.index() >= 0) {
                    data.put("index", (long) change.index());
                }
                if (change.action() == NexusListChange.Action.MOVE && change.oldIndex() >= 0) {
                    data.put("oldIndex", (long) change.oldIndex());
                }
                data.put("items", Collections.singletonList(codec.toJson(change.item())));
                if (change.action() == NexusListChange.Action.REPLACE) {
                    data.put("oldItems", Collections.singletonList(codec.toJson(change.oldItem())));
                }
                break;
            case REMOVE:
                if (change.oldIndex() >= 0) {
                    data.put("oldIndex", (long) change.oldIndex());
                }
                data.put("oldItems", Collections.singletonList(codec.toJson(change.oldItem())));
                break;
            default:
                break;
        }
        return data;
    }

    // ------------------------------------------------------------------ leaks (§5.1)

    boolean isOpen() {
        return !disposed && client.state() != ConnectionState.DISCONNECTED;
    }

    void audit(Leaks leaks) {
        boolean open = isOpen();
        if (open) {
            leaks.openClients++;
            leaks.details.add("client " + handle + " is " + state() + " and not disposed");
        }
        for (PipeEntry pipe : pipes) {
            if (pipe.isOpen()) {
                leaks.openPipes++;
                leaks.details.add("pipe " + pipe.handle + " of " + handle + " neither completed nor disposed");
            }
        }
        int pendingCalls = pendingInvocations();
        if (pendingCalls > 0) {
            leaks.pendingInvocations += pendingCalls;
            leaks.details.add(pendingCalls + " invocation(s) of " + handle + " still running");
        }
        // Watches only (§5.1, as the C# and JS hosts): an enabled collection ends with its client.
        int subscriptions = watches.size();
        if (subscriptions > 0) {
            leaks.subscriptions += subscriptions;
            leaks.details.add(subscriptions + " collection watch(es) of " + handle + " not ended");
        }
    }
}
