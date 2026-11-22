package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.session.SessionClosedException;
import com.dtronix.nexnet.testnexus.scaffolding.Scaffolding;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import java.io.IOException;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.BooleanSupplier;

/**
 * The JVM language host (control protocol, docs/internals/interop-control-protocol.md): reads requests, runs them
 * against real Java clients, and sends responses and events.
 *
 * <p>Threads: the control reader thread is dedicated to the control connection. It answers the host-level ops
 * ({@code ping}, {@code host.dumpThreads}, {@code log.level}, {@code host.shutdown}) and starts every other request in
 * arrival order: it applies the request's synchronous effect and hands the rest to futures (§6.1), so it never waits
 * for a client and a ping is answered while other ops run. Blocking work (closing clients and pools) runs on worker
 * threads; responses and events are written by whichever thread completes them, one whole frame at a time.
 */
final class InteropHost {
    /** How many tests the host runs at once (hello {@code maxConcurrency}). */
    static final int MAX_CONCURRENCY = 8;

    private static final List<String> CAPABILITIES = Arrays.asList(
        "AuthorizationHeader", "ClientCertificate", "Pinning", "CustomCa", "RemoteCertificate", "HttpSocket", "Tcp",
        "FakeTime", "ClientPool", "PipeInputCompletion", "ChannelReadCancellation");

    private static final List<String> TRANSPORTS = Arrays.asList(
        "Tcp", "TcpTls", "HttpSocket", "HttpSocketTls", "WebSocket", "WebSocketTls");

    /** The capabilities of the OkHttp mode ({@link Connectors}): no HttpSocket or TCP transport. */
    private static final List<String> OKHTTP_CAPABILITIES = Arrays.asList(
        "AuthorizationHeader", "ClientCertificate", "Pinning", "CustomCa", "RemoteCertificate", "FakeTime",
        "ClientPool", "PipeInputCompletion", "ChannelReadCancellation");

    private final ControlChannel channel;
    final ExecutorService workers = Executors.newCachedThreadPool(daemon("nexnet-host-worker"));
    final ScheduledExecutorService timer;
    private final Map<String, Scope> scopes = new ConcurrentHashMap<>();
    final Map<Long, CompletableFuture<Object>> forwards = new ConcurrentHashMap<>();
    private final AtomicLong handles = new AtomicLong();
    private final AtomicLong callIds = new AtomicLong();
    private final long epoch = System.nanoTime();
    private volatile NexusLogLevel logLevel = NexusLogLevel.WARNING;

    InteropHost(ControlChannel channel) {
        this.channel = channel;
        ScheduledThreadPoolExecutor scheduler = new ScheduledThreadPoolExecutor(1, daemon("nexnet-host-timer"));
        scheduler.setRemoveOnCancelPolicy(true);
        this.timer = scheduler;
    }

    private static ThreadFactory daemon(String name) {
        AtomicInteger count = new AtomicInteger();
        return runnable -> {
            Thread thread = new Thread(runnable, name + "-" + count.incrementAndGet());
            thread.setDaemon(true);
            return thread;
        };
    }

    long hostNs() {
        return System.nanoTime() - epoch + 1;
    }

    String newHandle(String prefix) {
        return prefix + handles.incrementAndGet();
    }

    long nextCallId() {
        return callIds.incrementAndGet();
    }

    // ------------------------------------------------------------------ messages

    void hello(String token) {
        Map<String, Object> hello = Protocol.map(
            "t", "hello",
            "proto", 1L,
            "token", token,
            "lang", "java",
            "runtime", System.getProperty("java.vm.name", "Java") + " " + System.getProperty("java.version", "?"),
            "lib", "com.dtronix:nexnet-client " + NexusRuntime.VERSION
                + (Connectors.okHttp() ? " (WebSocket: com.dtronix:nexnet-client-okhttp)" : ""),
            "schemaHash", Scaffolding.SCHEMA_HASH,
            "capabilities", Connectors.okHttp() ? OKHTTP_CAPABILITIES : CAPABILITIES,
            "transports", Connectors.okHttp() ? Connectors.OKHTTP_TRANSPORTS : TRANSPORTS,
            "maxConcurrency", (long) MAX_CONCURRENCY,
            "pid", ProcessHandle.current().pid());
        channel.send(hello);
    }

    void event(String test, String h, String ev, Map<String, Object> data) {
        channel.send(Protocol.map("t", "evt", "test", test, "h", h, "ev", ev, "data", data, "hostNs", hostNs()));
    }

    void log(NexusLogLevel level, String message) {
        if (level.ordinal() >= logLevel.ordinal() && logLevel != NexusLogLevel.NONE) {
            event(null, null, "log", Protocol.map("level", Names.pascal(level), "category", "host", "message", message));
        }
    }

    void hostError(String message, Throwable error) {
        event(null, null, "host.error", Protocol.map("message", message,
            "detail", Protocol.map("stack", error == null ? null : Errors.stack(error))));
    }

    private void respond(long id, Map<String, Object> value, Throwable error) {
        Map<String, Object> res = Protocol.map("t", "res", "id", id);
        if (error == null) {
            res.put("ok", true);
            res.put("value", value == null ? Protocol.empty() : value);
        } else {
            res.put("ok", false);
            res.put("error", Errors.toError(error));
        }
        res.put("hostNs", hostNs());
        channel.send(res);
    }

    private void bye(String reason) {
        channel.send(Protocol.map("t", "bye", "reason", reason));
    }

    /** Reads requests until the connection closes. */
    void run() {
        while (true) {
            Map<String, Object> message;
            try {
                message = channel.read();
            } catch (IOException e) {
                bye("Protocol error: " + e.getMessage());
                return;
            }
            if (message == null) {
                return;
            }
            Object type = message.get("t");
            if ("bye".equals(type)) {
                return;
            }
            if (!"req".equals(type)) {
                bye("Unknown message type '" + type + "'.");
                return;
            }
            if (!onRequest(message)) {
                return;
            }
        }
    }

    /** Handles one request; false after host.shutdown. */
    private boolean onRequest(Map<String, Object> message) {
        long id;
        String op;
        Object test;
        Args args;
        Long timeoutMs;
        try {
            id = Args.toLong("id", message.get("id"));
            op = (String) message.get("op");
            test = message.get("test");
            Object raw = message.get("args");
            if (raw != null && !(raw instanceof Map)) {
                throw HostError.badRequest("args must be an object.");
            }
            @SuppressWarnings("unchecked")
            Map<String, Object> map = (Map<String, Object>) raw;
            args = new Args(map);
            timeoutMs = Args.toLong("timeoutMs", message.get("timeoutMs"));
        } catch (RuntimeException e) {
            Object rawId = message.get("id");
            if (rawId instanceof Long) {
                respond((Long) rawId, null, HostError.badRequest("Malformed request: " + e.getMessage()));
            }
            return true;
        }
        if (op == null) {
            respond(id, null, HostError.badRequest("The request has no op."));
            return true;
        }
        switch (op) {
            case "ping":
                respond(id, Protocol.empty(), null);
                return true;
            case "host.dumpThreads":
                respond(id, Protocol.map("text", dumpThreads()), null);
                return true;
            case "log.level":
                try {
                    logLevel = Names.parse(NexusLogLevel.class, args.str("level"));
                    respond(id, Protocol.empty(), null);
                } catch (RuntimeException e) {
                    respond(id, null, e);
                }
                return true;
            case "host.shutdown":
                respond(id, Protocol.empty(), null);
                bye("host.shutdown");
                return false;
            default:
                String scope = test instanceof String ? (String) test : null;
                long bound = timeoutMs == null ? 0 : timeoutMs;
                // Started on this thread, in arrival order, with no queue hop: a script set right before the server
                // calls the client is in place when the call arrives (§6.1). Every op's synchronous part is short and
                // never blocks; blocking work runs on the workers.
                dispatch(id, scope, op, args, bound);
                return true;
        }
    }

    private void dispatch(long id, String test, String op, Args args, long timeoutMs) {
        CompletableFuture<Map<String, Object>> result;
        Scope scope = null;
        try {
            if (test == null) {
                throw HostError.badRequest("The op '" + op + "' needs a test scope.");
            }
            if (op.equals("test.begin")) {
                if (scopes.putIfAbsent(test, new Scope(test)) != null) {
                    throw HostError.badRequest("The test scope '" + test + "' is open already.");
                }
                result = CompletableFuture.completedFuture(Protocol.empty());
            } else {
                scope = scopes.get(test);
                if (scope == null) {
                    throw HostError.badRequest("The test scope '" + test + "' is not open.");
                }
                result = handle(scope, op, args);
            }
        } catch (Throwable e) {
            result = Async.failed(e);
        }
        CompletableFuture<Map<String, Object>> response = result;
        if (!response.isDone()) {
            if (scope != null) {
                scope.track(response);
            }
            if (timeoutMs > 0) {
                ScheduledFuture<?> bound = timer.schedule(() -> response.completeExceptionally(
                    new HostError(HostError.TIMEOUT, op + " did not complete within " + timeoutMs + " ms.")),
                    timeoutMs, TimeUnit.MILLISECONDS);
                response.whenComplete((v, e) -> bound.cancel(false));
            }
        }
        response.whenComplete((value, error) -> respond(id, value, error));
    }

    // ------------------------------------------------------------------ ops

    private CompletableFuture<Map<String, Object>> done(Map<String, Object> value) {
        return CompletableFuture.completedFuture(value);
    }

    private CompletableFuture<Map<String, Object>> handle(Scope scope, String op, Args args) {
        switch (op) {
            case "test.audit":
                return audit(scope, args);
            case "test.end":
                return end(scope);

            case "client.create": {
                ScaffoldingExport<?> export = export(args);
                String h = newHandle("c");
                ClientEntry client = ClientEntry.create(this, scope, h, export, args.obj("spec"));
                scope.put(h, client);
                return done(Protocol.map("h", h));
            }
            case "client.connect":
                return clientOnly(scope, args).connect();
            case "client.tryConnect":
                return clientOnly(scope, args).tryConnect();
            case "client.disconnect":
                return clientOnly(scope, args).disconnect();
            case "client.dispose": {
                ClientEntry client = clientOnly(scope, args);
                scope.remove(client.handle);
                return CompletableFuture.supplyAsync(() -> {
                    client.dispose();
                    return Protocol.empty();
                }, workers);
            }
            case "client.state":
                return done(Protocol.map("state", driven(scope, args).state()));
            case "client.remoteCertificate":
                return done(Protocol.map("der", clientOnly(scope, args).remoteCertificate()));
            case "client.isSecure":
                return done(Protocol.map("secure", clientOnly(scope, args).isSecure()));
            case "client.remoteEndpoint":
                return done(clientOnly(scope, args).remoteEndpoint());
            case "client.budget":
                return done(clientOnly(scope, args).budget());
            case "client.outstanding":
                return done(clientOnly(scope, args).outstanding());
            case "client.snapshot":
                return done(clientOnly(scope, args).snapshot());
            case "client.metrics":
                return done(clientOnly(scope, args).metrics());
            case "client.awaitState":
                return driven(scope, args).awaitState(args.str("state"));
            case "client.awaitReady":
                return driven(scope, args).awaitState(Names.pascal(ConnectionState.CONNECTED));
            case "client.awaitDisconnected":
                return driven(scope, args).awaitDisconnected();
            case "time.advance":
                return done(clientOnly(scope, args).advanceTime(args.integer("ms")));

            case "invoke":
                return driven(scope, args).invoke(args);
            case "future.await": {
                String handle = args.str("future");
                FutureEntry future = scope.get(handle, FutureEntry.class);
                CompletableFuture<Map<String, Object>> result = new CompletableFuture<>();
                future.future.whenComplete((v, e) -> {
                    scope.remove(handle);
                    if (e != null) {
                        result.completeExceptionally(e);
                    } else {
                        result.complete(Protocol.map("result", v));
                    }
                });
                return result;
            }
            case "future.poll": {
                String handle = args.str("future");
                FutureEntry future = scope.get(handle, FutureEntry.class);
                if (!future.future.isDone()) {
                    return done(Protocol.map("done", false));
                }
                scope.remove(handle);
                try {
                    return done(Protocol.map("done", true, "ok", true, "result", future.future.join()));
                } catch (RuntimeException e) {
                    return done(Protocol.map("done", true, "ok", false, "error", Errors.toError(e)));
                }
            }
            case "token.create": {
                String name = args.str("token");
                if (scope.tokens().putIfAbsent(name, new CancellationTokenSource()) != null) {
                    throw HostError.badRequest("The token '" + name + "' exists already.");
                }
                return done(Protocol.empty());
            }
            case "token.cancel": {
                CancellationTokenSource source = scope.tokens().get(args.str("token"));
                if (source != null) {
                    source.cancel();
                }
                return done(Protocol.empty());
            }
            case "token.dispose": {
                CancellationTokenSource source = scope.tokens().remove(args.str("token"));
                if (source != null) {
                    source.close();
                }
                return done(Protocol.empty());
            }

            case "script.set":
                return done(clientOnly(scope, args).setScript(args));
            case "script.clear":
                return done(clientOnly(scope, args).clearScript(args));
            case "signal":
                return done(clientOnly(scope, args).signal(args.str("name")));
            case "callback.reply": {
                clientOnly(scope, args);
                long callId = args.integer("callId");
                CompletableFuture<Object> forward = forwards.remove(callId);
                if (forward != null) {
                    Args error = args.optObj("error");
                    if (error != null) {
                        forward.completeExceptionally(Errors.nativeException(error.optStr("kind"),
                            error.optStr("message")));
                    } else {
                        forward.complete(args.value("result"));
                    }
                }
                return done(Protocol.empty());
            }

            case "pipe.create":
                return done(Protocol.map("pipe", clientOnly(scope, args).createPipe().handle));
            case "pipe.ready":
                return canceledOnSessionEnd(pipe(scope, args).pipe.ready());
            case "pipe.write":
                return pipe(scope, args).write(args);
            case "pipe.read":
                return pipe(scope, args).read(args.int32("min"), args.int32("max"));
            case "pipe.drain":
                return pipe(scope, args).drain();
            case "pipe.complete":
                return pipe(scope, args).pipe.completeOutput().thenApply(v -> Protocol.empty());
            case "pipe.whenComplete": {
                PipeEntry pipe = pipe(scope, args);
                // A pipe the session's end tore down before it was ready never completed: C# cancels its CompleteTask.
                return canceledOnSessionEnd(pipe.pipe.completed().thenCompose(v -> pipe.pipe.ready()));
            }
            case "pipe.accept":
                return clientOnly(scope, args).acceptPipe(args.str("name"));
            case "pipe.tryWrite":
                return pipe(scope, args).tryWrite(args);
            case "pipe.completeInput":
                return pipe(scope, args).completeInput();
            case "pipe.dispose": {
                Object value = scope.find(args.str("pipe"));
                if (value instanceof PipeEntry) {
                    ((PipeEntry) value).dispose();
                }
                return done(Protocol.empty());
            }

            case "channel.writer":
                return pipe(scope, args).channelWriter(args.str("type"));
            case "channel.write":
                return pipe(scope, args).channelWrite(args.list("items"));
            case "channel.complete":
                return pipe(scope, args).channelComplete();
            case "channel.reader":
                return pipe(scope, args).channelReader(args.str("type"));
            case "channel.read": {
                // A canceled read fails with kind Canceled and consumes nothing (capability ChannelReadCancellation).
                PipeEntry pipe = pipe(scope, args);
                int max = args.int32("max");
                Args cancel = args.optObj("cancel");
                if (cancel == null || !cancel.has("afterMs")) {
                    return pipe.channelRead(max, cancelToken(scope, args));
                }
                CancellationTokenSource after = new CancellationTokenSource();
                ScheduledFuture<?> timeout = timer.schedule(after::cancel, cancel.integer("afterMs"), TimeUnit.MILLISECONDS);
                CompletableFuture<Map<String, Object>> read = pipe.channelRead(max, after.token());
                read.whenComplete((v, e) -> timeout.cancel(false));
                return read;
            }
            case "channel.readAll":
                return pipe(scope, args).channelReadAll();

            case "list.enable":
                return clientOnly(scope, args).listEnable(args, cancelToken(scope, args));
            case "list.disable":
                return clientOnly(scope, args).listDisable(args);
            case "list.op":
                return clientOnly(scope, args).listOp(args);
            case "list.snapshot":
                return done(clientOnly(scope, args).listSnapshot(args));
            case "list.state":
                return done(clientOnly(scope, args).listState(args));
            case "list.watch":
                return done(clientOnly(scope, args).listWatch(args));
            case "list.unwatch":
                return done(clientOnly(scope, args).listUnwatch(args));

            case "pool.create": {
                ScaffoldingExport<?> export = export(args);
                Args options = args.optObj("options");
                String h = newHandle("pool");
                PoolEntry pool = PoolEntry.create(this, scope, h, export, args.obj("spec"),
                    options == null ? new Args(null) : options);
                scope.put(h, pool);
                return done(Protocol.map("pool", h));
            }
            case "pool.rent":
                return pool(scope, args).rent(cancelToken(scope, args));
            case "pool.return": {
                PoolEntry pool = pool(scope, args);
                RentalEntry rental = scope.get(args.str("h"), RentalEntry.class);
                return CompletableFuture.supplyAsync(() -> pool.giveBack(rental), workers);
            }
            case "pool.ensureConnected": {
                Object value = scope.find(args.str("h"));
                if (!(value instanceof RentalEntry)) {
                    throw HostError.unknownHandle(args.str("h"));
                }
                return ((RentalEntry) value).ensureConnected();
            }
            case "pool.state":
                return done(pool(scope, args).state());
            case "pool.advanceTime":
                return done(pool(scope, args).advanceTime(args.integer("ms")));
            case "pool.dispose": {
                PoolEntry pool = pool(scope, args);
                return CompletableFuture.supplyAsync(() -> {
                    pool.dispose();
                    return Protocol.empty();
                }, workers);
            }
            default:
                throw HostError.badRequest("Unknown op '" + op + "'.");
        }
    }

    /**
     * The Java pipe fails {@code ready()} and {@code completed()} with a {@link SessionClosedException} when the session
     * ends; the C# pipe cancels its ReadyTask and CompleteTask: the kind is {@code Canceled} (§6.7).
     */
    private static CompletableFuture<Map<String, Object>> canceledOnSessionEnd(CompletableFuture<Void> future) {
        CompletableFuture<Map<String, Object>> result = new CompletableFuture<>();
        future.whenComplete((v, error) -> {
            Throwable e = error == null ? null : Errors.unwrap(error);
            if (e == null) {
                result.complete(Protocol.empty());
            } else if (e instanceof SessionClosedException) {
                CancellationException canceled = new CancellationException("The session ended: " + e.getMessage());
                canceled.initCause(e);
                result.completeExceptionally(canceled);
            } else {
                result.completeExceptionally(e);
            }
        });
        return result;
    }

    private static ScaffoldingExport<?> export(Args args) {
        Args nexus = args.obj("nexus");
        String client = nexus.str("client");
        String server = nexus.str("server");
        ScaffoldingExport<?> export = Scaffolding.find(client, server);
        if (export == null) {
            throw HostError.notSupported("The JVM host has no generated code for the nexus pair " + client + " / "
                + server + ".");
        }
        return export;
    }

    private static Driven driven(Scope scope, Args args) {
        String h = args.str("h");
        Object value = scope.find(h);
        if (value instanceof Driven) {
            return (Driven) value;
        }
        throw HostError.unknownHandle(h);
    }

    private static ClientEntry clientOnly(Scope scope, Args args) {
        String h = args.str("h");
        Object value = scope.find(h);
        if (value instanceof RentalEntry) {
            throw HostError.notSupported("The op is not supported on a rented client: the pool owns its connection.");
        }
        if (value instanceof ClientEntry) {
            return (ClientEntry) value;
        }
        throw HostError.unknownHandle(h);
    }

    private static PipeEntry pipe(Scope scope, Args args) {
        return scope.get(args.str("pipe"), PipeEntry.class);
    }

    private static PoolEntry pool(Scope scope, Args args) {
        return scope.get(args.str("pool"), PoolEntry.class);
    }

    private static CancellationToken cancelToken(Scope scope, Args args) {
        Args cancel = args.optObj("cancel");
        if (cancel == null || !cancel.has("token")) {
            return CancellationToken.NONE;
        }
        return scope.token(cancel.str("token")).token();
    }

    // ------------------------------------------------------------------ scopes and leaks (§5)

    /** Waits up to 1 s while invocations are pending or clients are closing, so completions in flight settle. */
    private CompletableFuture<Void> settle(BooleanSupplier busy) {
        CompletableFuture<Void> done = new CompletableFuture<>();
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(1);
        Runnable[] poll = new Runnable[1];
        poll[0] = () -> {
            if (!busy.getAsBoolean() || System.nanoTime() >= deadline) {
                done.complete(null);
            } else {
                timer.schedule(poll[0], 10, TimeUnit.MILLISECONDS);
            }
        };
        poll[0].run();
        return done;
    }

    private static boolean busy(Scope scope) {
        for (Driven driven : scope.all(Driven.class)) {
            if (driven.pendingInvocations() > 0) {
                return true;
            }
            if (driven instanceof ClientEntry && !((ClientEntry) driven).isDisposed()
                && driven.client().state() == ConnectionState.DISCONNECTING) {
                return true;
            }
        }
        for (PoolEntry pool : scope.all(PoolEntry.class)) {
            if (pool.isSettling()) {
                return true;
            }
        }
        return false;
    }

    private CompletableFuture<Map<String, Object>> audit(Scope scope, Args args) {
        return settle(() -> busy(scope)).thenApply(v -> {
            Leaks leaks = new Leaks();
            if (args.has("h")) {
                Driven driven = driven(scope, args);
                if (driven instanceof ClientEntry) {
                    ((ClientEntry) driven).audit(leaks);
                } else {
                    auditRental((RentalEntry) driven, leaks);
                }
            } else if (args.has("pool")) {
                PoolEntry pool = pool(scope, args);
                pool.audit(leaks);
                for (RentalEntry rental : scope.all(RentalEntry.class)) {
                    if (rental.pool == pool) {
                        auditRental(rental, leaks);
                    }
                }
            } else {
                auditScope(scope, leaks);
            }
            return leaks.toJson();
        });
    }

    private static void auditRental(RentalEntry rental, Leaks leaks) {
        int pending = rental.pendingInvocations();
        if (pending > 0) {
            leaks.pendingInvocations += pending;
            leaks.details.add(pending + " invocation(s) of rental " + rental.handle + " still running");
        }
    }

    private static void auditScope(Scope scope, Leaks leaks) {
        for (ClientEntry client : scope.all(ClientEntry.class)) {
            client.audit(leaks);
        }
        for (PoolEntry pool : scope.all(PoolEntry.class)) {
            pool.audit(leaks);
        }
        for (RentalEntry rental : scope.all(RentalEntry.class)) {
            auditRental(rental, leaks);
        }
    }

    private CompletableFuture<Map<String, Object>> end(Scope scope) {
        CompletableFuture<Map<String, Object>> result = new CompletableFuture<>();
        settle(() -> busy(scope)).whenComplete((v, e) -> {
            Leaks leaks = new Leaks();
            try {
                auditScope(scope, leaks);
            } catch (RuntimeException failure) {
                result.completeExceptionally(failure);
                return;
            }
            scopes.remove(scope.id, scope);
            for (CompletableFuture<?> request : scope.inflight()) {
                if (request != result) {
                    request.completeExceptionally(new CancellationException("The test scope ended."));
                }
            }
            result.complete(leaks.toJson());
            workers.execute(() -> release(scope));
        });
        return result;
    }

    /** Releases everything a scope still holds (§5); blocks while clients close. */
    private void release(Scope scope) {
        try {
            for (CancellationTokenSource source : scope.tokens().values()) {
                source.cancel();
            }
            for (RentalEntry rental : scope.all(RentalEntry.class)) {
                rental.release();
            }
            for (PoolEntry pool : scope.all(PoolEntry.class)) {
                pool.dispose();
            }
            for (ClientEntry client : scope.all(ClientEntry.class)) {
                client.dispose();
            }
            for (FutureEntry future : scope.all(FutureEntry.class)) {
                future.future.completeExceptionally(new CancellationException("The test scope ended."));
            }
            for (CancellationTokenSource source : scope.tokens().values()) {
                source.close();
            }
            scope.tokens().clear();
            scope.clearHandles();
        } catch (RuntimeException e) {
            hostError("Releasing the test scope " + scope.id + " failed.", e);
        }
    }

    private static String dumpThreads() {
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<Thread, StackTraceElement[]> entry : Thread.getAllStackTraces().entrySet()) {
            Thread thread = entry.getKey();
            sb.append('"').append(thread.getName()).append('"')
                .append(thread.isDaemon() ? " daemon" : "")
                .append(" id=").append(thread.getId())
                .append(' ').append(thread.getState()).append('\n');
            for (StackTraceElement frame : entry.getValue()) {
                sb.append("    at ").append(frame).append('\n');
            }
            sb.append('\n');
        }
        return sb.toString();
    }
}
