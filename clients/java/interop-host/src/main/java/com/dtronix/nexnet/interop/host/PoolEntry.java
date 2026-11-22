package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.pool.NexusClientPool;
import com.dtronix.nexnet.pool.NexusClientPoolConfig;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.pool.NexusClientPoolConnectionException;
import com.dtronix.nexnet.pool.NexusCollectionConnector;
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
import java.util.concurrent.ConcurrentHashMap;
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
    /** Collection connectors by the runner's name ({@code pool.collection}, §6.11). */
    private final Map<String, Connector> connectors = new ConcurrentHashMap<>();
    private volatile boolean disposed;

    /** A collection connector of the pool: the runtime's connector, created on first use. */
    private static final class Connector {
        final String name;
        final String collection;
        NexusCollectionConnector<NexusList<Object>> connector;
        /** The server proxy the selector saw last: the rented client's. */
        volatile Object selected;
        volatile boolean rented;
        boolean released;
        final List<CollectionEntry> entries = new CopyOnWriteArrayList<>();

        Connector(String name, String collection) {
            this.name = name;
            this.collection = collection;
        }
    }

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
            completeFailure(done, error);
        });
        return done;
    }

    /** Completes a failed rent ({@code pool.rent}, {@code pool.collection}): a connection failure is a result (§6.11). */
    private static void completeFailure(CompletableFuture<Map<String, Object>> done, Throwable error) {
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
    }

    /**
     * {@code pool.collection} (§6.11): the connector named {@code connectorName}, created on first use for
     * {@code collection}, rents a client; its collection gets a handle for the {@code list.*} ops.
     */
    CompletableFuture<Map<String, Object>> collection(String connectorName, String collection) {
        if (disposed) {
            throw new HostError(HostError.INVALID_OPERATION, "The pool is disposed.");
        }
        if (export.collection(collection) == null) {
            throw HostError.badRequest("Unknown collection '" + collection + "' of " + export + ".");
        }
        Connector state;
        synchronized (connectors) {
            state = connectors.get(connectorName);
            if (state == null) {
                state = new Connector(connectorName, collection);
                state.connector = newConnector(pool, state);
                connectors.put(connectorName, state);
            } else if (!state.collection.equals(collection)) {
                throw HostError.badRequest("The connector '" + connectorName + "' is for the collection '"
                    + state.collection + "', not '" + collection + "'.");
            }
            if (state.released) {
                throw new HostError(HostError.INVALID_OPERATION, "The collection connector '" + connectorName
                    + "' is released.");
            }
        }
        Connector connector = state;
        CompletableFuture<Map<String, Object>> done = new CompletableFuture<>();
        connector.connector.collection().whenComplete((list, error) -> {
            if (error != null) {
                completeFailure(done, error);
                return;
            }
            connector.rented = true;
            Pooled pooled = pooledOf(connector.selected);
            String h = host.newHandle("h");
            CollectionEntry entry = new CollectionEntry(host, scope, h, this, connector.collection, list);
            boolean released;
            synchronized (connectors) {
                released = connector.released;
                if (!released) {
                    connector.entries.add(entry);
                    scope.put(h, entry);
                }
            }
            if (released) {
                // Released while it rented: the release gives back this client too.
                connector.connector.close();
                done.completeExceptionally(new HostError(HostError.INVALID_OPERATION,
                    "The collection connector '" + connector.name + "' was released."));
                return;
            }
            done.complete(Protocol.map("h", h, "client", pooled == null ? "pc?" : pooled.id));
        });
        return done;
    }

    @SuppressWarnings("unchecked")
    private <P> NexusCollectionConnector<NexusList<Object>> newConnector(NexusClientPool<P> typed, Connector state) {
        String collection = state.collection;
        return typed.collectionConnector(proxy -> {
            state.selected = proxy;
            return (NexusList<Object>) export.listUnchecked(proxy, collection);
        });
    }

    /**
     * {@code pool.collectionRelease} (§6.11): the runtime's {@code close()}, then the connector's collection handles.
     * Blocks while the client goes back (run it off the dispatcher). An unknown or released connector is ignored.
     */
    Map<String, Object> releaseCollection(String connectorName) {
        Connector connector = connectors.get(connectorName);
        if (connector != null) {
            release(connector);
        }
        return Protocol.empty();
    }

    /** Releases every collection connector ({@code test.end}). */
    void releaseCollections() {
        for (Connector connector : connectors.values()) {
            release(connector);
        }
    }

    private void release(Connector connector) {
        synchronized (connectors) {
            if (connector.released) {
                return;
            }
            connector.released = true;
        }
        connector.connector.close();
        for (CollectionEntry entry : connector.entries) {
            entry.lists.close();
            scope.remove(entry.handle);
        }
        connector.entries.clear();
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
        for (Connector connector : connectors.values()) {
            synchronized (connectors) {
                if (connector.rented && !connector.released) {
                    leaks.openClients++;
                    leaks.details.add("the collection connector " + connector.name + " of pool " + handle
                        + " is not released: it holds a rented client");
                }
            }
            for (CollectionEntry entry : connector.entries) {
                int subscriptions = entry.lists.watchCount();
                if (subscriptions > 0) {
                    leaks.subscriptions += subscriptions;
                    leaks.details.add(subscriptions + " collection watch(es) of " + entry.handle + " not ended");
                }
            }
        }
    }
}
