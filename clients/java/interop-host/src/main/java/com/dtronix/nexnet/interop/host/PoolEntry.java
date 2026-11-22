package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.pool.NexusClientPool;
import com.dtronix.nexnet.pool.NexusClientPoolConfig;
import com.dtronix.nexnet.pool.NexusClientPoolConnectionException;
import com.dtronix.nexnet.pool.RentedNexusClient;
import com.dtronix.nexnet.testnexus.scaffolding.ScaffoldingExport;
import com.dtronix.nexnet.testnexus.scaffolding.ScriptHost;
import java.util.Arrays;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

/** A client pool (control protocol §6.11). */
final class PoolEntry {
    private static final Set<String> OPTIONS = new HashSet<>(Arrays.asList(
        "maxConnections", "maxIdleTimeMs", "minIdleConnections"));

    /** The nexus of pooled clients: not scripted, every call completes with its default result. */
    private static final ScriptHost UNSCRIPTED = call -> CompletableFuture.completedFuture(null);

    final InteropHost host;
    final Scope scope;
    final String handle;
    final ScaffoldingExport<?> export;
    final FakeTimers fakeTime;
    private final NexusClientPool<?> pool;
    private final List<NexusClient<?>> clients = new CopyOnWriteArrayList<>();
    private final Map<Object, Pooled> byProxy = new IdentityHashMap<>();
    private final AtomicInteger nextClient = new AtomicInteger();
    final AtomicInteger rented = new AtomicInteger();
    private volatile boolean disposed;

    /** A client the pool created. */
    static final class Pooled {
        final String id;
        final NexusClient<?> client;

        Pooled(String id, NexusClient<?> client) {
            this.id = id;
            this.client = client;
        }
    }

    private PoolEntry(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export, Args spec,
                      Args options) {
        this.host = host;
        this.scope = scope;
        this.handle = handle;
        this.export = export;
        for (String member : options.raw().keySet()) {
            if (!OPTIONS.contains(member)) {
                throw HostError.notSupported("The JVM host does not know the pool option '" + member + "'.");
            }
        }
        Long maxConnections = options.optInteger("maxConnections");
        Long maxIdleTime = options.optInteger("maxIdleTimeMs");
        Long minIdle = options.optInteger("minIdleConnections");
        if ((maxConnections != null && maxConnections < 1) || (maxIdleTime != null && maxIdleTime < 0)
            || (minIdle != null && minIdle < 0)) {
            throw HostError.badRequest("A pool option is out of range.");
        }
        String level = spec.optStr("logLevel");
        ClientLogger logger = level == null ? null
            : new ClientLogger(host, scope.id, handle, Names.parse(NexusLogLevel.class, level));
        if (spec.has("onInvocationException")) {
            // Control protocol §6.4.1: the client nexus of pooled clients is not scripted.
            throw HostError.notSupported("The client nexus of pooled clients is not scripted: ClientSpec.onInvocationException "
                + "needs a client.");
        }
        if (spec.has("metrics")) {
            throw HostError.notSupported("ClientSpec.metrics needs a client: the metrics of pooled clients are not recorded.");
        }
        SpecMapper.Setup setup = SpecMapper.map(spec, logger, null);
        this.fakeTime = setup.fakeTime;

        NexusClientPoolConfig config = new NexusClientPoolConfig(setup.config);
        if (maxConnections != null) {
            config.maxConnections(maxConnections.intValue());
        }
        if (maxIdleTime != null) {
            // The Java pool needs a positive idle time; 0 (evict at once) is 1 ms.
            config.maxIdleTimeMillis(Math.max(1, maxIdleTime));
        }
        if (minIdle != null) {
            config.minIdleConnections(minIdle.intValue());
        }
        this.pool = newPool(export, config, setup);
    }

    static PoolEntry create(InteropHost host, Scope scope, String handle, ScaffoldingExport<?> export, Args spec,
                            Args options) {
        return new PoolEntry(host, scope, handle, export, spec, options);
    }

    private <P> NexusClientPool<P> newPool(ScaffoldingExport<P> typed, NexusClientPoolConfig config,
                                           SpecMapper.Setup setup) {
        return new NexusClientPool<>(config, () -> {
            NexusClient<P> client = NexusClient.create(setup.config, typed.handshake(),
                typed.createNexus(UNSCRIPTED), typed.proxyFactory(), setup.connector, setup.timers,
                setup.sessionOptions);
            Pooled pooled = new Pooled("pc" + nextClient.incrementAndGet(), client);
            clients.add(client);
            synchronized (byProxy) {
                byProxy.put(client.proxy(), pooled);
            }
            return client;
        }, setup.timers);
    }

    private Pooled pooledOf(Object proxy) {
        synchronized (byProxy) {
            return byProxy.get(proxy);
        }
    }

    CompletableFuture<Map<String, Object>> rent(CancellationToken token) {
        if (disposed) {
            throw new HostError(HostError.INVALID_OPERATION, "The pool is disposed.");
        }
        CompletableFuture<Map<String, Object>> done = new CompletableFuture<>();
        pool.rent(token).whenComplete((rental, error) -> {
            if (error == null) {
                Pooled pooled = pooledOf(rental.proxy());
                String h = host.newHandle("h");
                RentalEntry entry = new RentalEntry(host, scope, h, this, rental,
                    pooled == null ? null : pooled.client);
                rented.incrementAndGet();
                scope.put(h, entry);
                done.complete(Protocol.map("h", h, "client", pooled == null ? "pc?" : pooled.id));
                return;
            }
            Throwable e = Errors.unwrap(error);
            if (e instanceof NexusClientPoolConnectionException) {
                done.complete(Protocol.map("failed", failure(((NexusClientPoolConnectionException) e).result(),
                    e.getMessage())));
            } else if (e instanceof CancellationException
                && "The connection attempt was canceled.".equals(e.getMessage())) {
                // A cancel during the connection attempt ends it as a connection failure (§6.11).
                Map<String, Object> failed = Protocol.map("state", "Canceled", "reason", "None",
                    "message", "Failed to connect after renting.", "error", Errors.toError(e));
                done.complete(Protocol.map("failed", failed));
            } else {
                done.completeExceptionally(e);
            }
        });
        return done;
    }

    private static Map<String, Object> failure(ConnectionResult result, String message) {
        Map<String, Object> failed = Protocol.map("state", Names.pascal(result.status()),
            "reason", Names.pascal(result.disconnectReason()), "message", message);
        if (result.exception() != null) {
            failed.put("error", Errors.toError(result.exception()));
        }
        return failed;
    }

    Map<String, Object> giveBack(RentalEntry rental) {
        rental.release();
        scope.remove(rental.handle);
        return Protocol.empty();
    }

    void onReturned() {
        rented.decrementAndGet();
    }

    Map<String, Object> state() {
        return Protocol.map("maxConnections", (long) pool.maxConnections(), "idle", (long) pool.availableConnections(),
            "rented", (long) rented.get());
    }

    Map<String, Object> advanceTime(long millis) {
        if (fakeTime == null) {
            throw new HostError(HostError.INVALID_OPERATION, "Create the pool with ClientSpec.fakeTime to move its clock.");
        }
        if (fakeTime.advance(millis) > 0) {
            // The pool's timer hands the health check to the runtime's pool; run it here so it is done before the
            // response. A second pass over the idle clients is harmless.
            pool.checkHealth();
        }
        return Protocol.empty();
    }

    /** Disposes the pool and its idle clients; blocks while they close (run it off the dispatcher). */
    void dispose() {
        disposed = true;
        pool.close();
    }

    boolean isDisposed() {
        return disposed;
    }

    /**
     * Whether a client of the pool is still closing. The pool closes evicted and returned clients off the calling
     * thread (as the C# and JS pools do), so after a health-check eviction the client can still be Connected when
     * the pool is disposed and the audit starts: once disposed, every client that is not Disconnected is settling,
     * like the leak audits of the C# in-proc driver and the JS host, which wait up to 1 s for it.
     */
    boolean isSettling() {
        for (NexusClient<?> client : clients) {
            ConnectionState state = client.state();
            if (state == ConnectionState.DISCONNECTING || (disposed && state != ConnectionState.DISCONNECTED)) {
                return true;
            }
        }
        return false;
    }

    void audit(Leaks leaks) {
        for (NexusClient<?> client : clients) {
            ConnectionState state = client.state();
            if (state != ConnectionState.DISCONNECTED) {
                leaks.openClients++;
                leaks.details.add("a client of pool " + handle + " is " + Names.pascal(client.state()));
            }
        }
        if (!disposed) {
            leaks.timers++;
            leaks.details.add("pool " + handle + " is not disposed (health check)");
        }
    }
}
