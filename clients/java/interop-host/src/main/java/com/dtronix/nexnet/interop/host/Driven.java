package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

/** A client the runner drives through a handle: a client of {@code client.create} or a pool rental. */
abstract class Driven {
    final InteropHost host;
    final Scope scope;
    final String handle;
    final ScaffoldingExport<?> export;
    final AtomicInteger pending = new AtomicInteger();

    Driven(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export) {
        this.host = host;
        this.scope = scope;
        this.handle = handle;
        this.export = export;
    }

    /** The Java client. */
    abstract NexusClient<?> client();

    /** The server proxy to invoke on. */
    abstract Object proxy();

    /** The session to report the disconnect reason of when none is current. */
    NexusSession lastSession() {
        return null;
    }

    /** The disconnect reason of the last failed connection attempt, or null. */
    String lastFailure() {
        return null;
    }

    /**
     * The pipe or channel argument of a pipe or channel method.
     *
     * @param pipeHandle the pipe handle of the request
     * @param method the method
     * @return the {@code NexusDuplexPipe} or {@code NexusDuplexChannel}
     */
    abstract Object pipeArgument(String pipeHandle, ScaffoldingExport.Method method);

    String state() {
        return Names.pascal(client().state());
    }

    CompletableFuture<Map<String, Object>> awaitState(String name) {
        ConnectionState target = Names.parse(ConnectionState.class, name);
        NexusClient<?> client = client();
        CompletableFuture<Map<String, Object>> done = new CompletableFuture<>();
        Registration registration = client.addStateListener(state -> {
            if (state == target) {
                done.complete(Protocol.empty());
            }
        });
        done.whenComplete((v, e) -> registration.close());
        if (client.state() == target) {
            done.complete(Protocol.empty());
        }
        return done;
    }

    CompletableFuture<Map<String, Object>> awaitDisconnected() {
        NexusClient<?> client = client();
        NexusSession current = client.session();
        if (current != null) {
            // The end of this session, whether or not the client then reconnects (§6.4).
            return current.closed().handle((reason, error) -> Protocol.map("reason", reasonName(current)));
        }
        if (client.state() == ConnectionState.CONNECTING) {
            return client.disconnected().thenApply(v -> Protocol.map("reason", lastReason()));
        }
        return CompletableFuture.completedFuture(Protocol.map("reason", lastReason()));
    }

    /** The reason the last session or connection attempt ended. */
    private String lastReason() {
        String failure = lastFailure();
        return failure != null ? failure : reasonName(lastSession());
    }

    static String reasonName(NexusSession session) {
        if (session == null) {
            return "None";
        }
        DisconnectReason reason = session.disconnectReason();
        return reason == null || reason == DisconnectReason.UNKNOWN ? "None" : Names.pascal(reason);
    }

    CompletableFuture<Map<String, Object>> invoke(Args args) {
        String name = args.obj("method").str("name");
        ScaffoldingExport.Method method = export.method(name);
        if (method == null) {
            throw HostError.badRequest("Unknown method '" + name + "' of " + export + ".");
        }
        List<Object> arguments = args.list("args");
        Object pipe = null;
        if (method.pipeKind() != ScaffoldingExport.PipeKind.NONE) {
            String pipeHandle = args.optStr("pipe");
            if (pipeHandle == null) {
                throw HostError.badRequest(name + " needs a pipe.");
            }
            pipe = pipeArgument(pipeHandle, method);
        }

        CancellationToken token = CancellationToken.NONE;
        CancellationTokenSource after = null;
        long afterMs = 0;
        Args cancel = args.optObj("cancel");
        if (cancel != null) {
            if (cancel.has("afterMs")) {
                after = new CancellationTokenSource();
                token = after.token();
                afterMs = cancel.integer("afterMs");
            } else if (cancel.has("token")) {
                token = scope.token(cancel.str("token")).token();
            }
        }

        pending.incrementAndGet();
        CompletableFuture<Object> call;
        try {
            call = export.invokeUnchecked(proxy(), name, arguments, pipe, token);
        } catch (RuntimeException e) {
            pending.decrementAndGet();
            throw e;
        }
        CancellationTokenSource source = after;
        ScheduledFuture<?> timer = source == null ? null
            : host.timer.schedule(source::cancel, afterMs, TimeUnit.MILLISECONDS);
        call.whenComplete((v, e) -> {
            pending.decrementAndGet();
            if (timer != null) {
                timer.cancel(false);
            }
        });

        if (args.bool("detach") || method.returnKind() == ScaffoldingExport.ReturnKind.VOID) {
            String future = host.newHandle("f");
            scope.put(future, new FutureEntry(call));
            return CompletableFuture.completedFuture(Protocol.map("future", future));
        }
        return call.thenApply(v -> Protocol.map("result", v));
    }

    /** The invocations of this client not completed. */
    int pendingInvocations() {
        return pending.get();
    }
}
