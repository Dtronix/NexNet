package com.dtronix.nexnet.pool;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.SessionTimers;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Function;
import java.util.function.Supplier;
import org.jspecify.annotations.Nullable;

/**
 * A pool of connected clients, like the C# {@code NexusClientPool}: {@link #rent()} hands out a connected client,
 * reusing an idle one when it is healthy and connecting a new one otherwise, and closing the rental returns it. At
 * most {@link #maxConnections()} clients are rented at once; further rents wait, first come first served, without
 * blocking a thread.
 *
 * <p>A health check runs every quarter of {@code maxIdleTime} (between 100 ms and 30 s): it closes idle clients that
 * lost their connection, and idle clients beyond {@code minIdleConnections} that have been idle longer than
 * {@code maxIdleTime}.
 *
 * <p>Thread-safe. Futures complete on the runtime's shared pool or the clients' callback executor.
 *
 * @param <TProxy> the generated server proxy type
 */
public final class NexusClientPool<TProxy> implements AutoCloseable {
    private final NexusClientPoolConfig config;
    private final Supplier<NexusClient<TProxy>> clientFactory;
    private final SessionTimers timers;
    private final Executor executor = NexusExecutors.shared();
    private final AsyncSemaphore semaphore;
    private final long healthCheckIntervalMillis;
    private final @Nullable NexusLogger logger;

    private final Object lock = new Object();
    private final LinkedList<PooledClient<TProxy>> idle = new LinkedList<>();
    private @Nullable Registration healthCheck;
    private volatile boolean closed;

    /** A client owned by the pool. */
    private static final class PooledClient<TProxy> {
        final NexusClient<TProxy> client;
        final AtomicBoolean closed = new AtomicBoolean();
        volatile long lastUsedNanos;

        PooledClient(NexusClient<TProxy> client, long now) {
            this.client = client;
            this.lastUsedNanos = now;
        }

        boolean isHealthy() {
            return !closed.get() && client.state() == ConnectionState.CONNECTED;
        }
    }

    /**
     * Creates a pool.
     *
     * @param config the pool configuration
     * @param clientFactory creates a new, unconnected client (for example, {@code () -> ChatClient.create(config,
     *                      new MyNexus())})
     */
    public NexusClientPool(NexusClientPoolConfig config, Supplier<NexusClient<TProxy>> clientFactory) {
        this(config, clientFactory, SessionTimers.system());
    }

    /**
     * Creates a pool on a custom clock and scheduler (for tests).
     *
     * @param config the pool configuration
     * @param clientFactory creates a new, unconnected client
     * @param timers the clock of the idle times and the scheduler of the health check
     */
    public NexusClientPool(NexusClientPoolConfig config, Supplier<NexusClient<TProxy>> clientFactory,
                           SessionTimers timers) {
        if (config == null || clientFactory == null || timers == null) {
            throw new NullPointerException("An argument is null.");
        }
        this.config = config;
        this.clientFactory = clientFactory;
        this.timers = timers;
        this.semaphore = new AsyncSemaphore(config.maxConnections());
        this.healthCheckIntervalMillis = Math.max(100, Math.min(30_000, config.maxIdleTimeMillis() / 4));
        NexusLogger root = config.clientConfig().logger();
        this.logger = root == null ? null : root.createLogger("Pool");
        PoolCounters.HEALTH_CHECKS.incrementAndGet();
        synchronized (lock) {
            healthCheck = timers.schedule(this::onHealthCheckTimer, healthCheckIntervalMillis);
        }
    }

    /**
     * Rents a connected client, connecting a new one when none is idle and the pool is not full.
     *
     * @return a future of the rented client; it fails with {@link NexusClientPoolConnectionException} when a new
     *     client cannot connect, and with {@link IllegalStateException} when the pool is closed
     */
    public CompletableFuture<RentedNexusClient<TProxy>> rent() {
        return rent(CancellationToken.NONE);
    }

    /**
     * Rents a connected client, with cancellation.
     *
     * @param cancellationToken cancels the wait for a free slot and the connection of a new client
     * @return a future of the rented client; it fails with a {@link CancellationException} when
     *     canceled
     */
    public CompletableFuture<RentedNexusClient<TProxy>> rent(CancellationToken cancellationToken) {
        CancellationToken token = cancellationToken == null ? CancellationToken.NONE : cancellationToken;
        CompletableFuture<RentedNexusClient<TProxy>> result = new CompletableFuture<>();
        if (closed) {
            result.completeExceptionally(new IllegalStateException("The pool is closed."));
            return result;
        }
        semaphore.acquire(token).whenComplete((v, e) -> {
            if (e != null) {
                result.completeExceptionally(e);
                return;
            }
            NexusExecutors.execute(executor, () -> {
                try {
                    afterAcquire(result, token);
                } catch (RuntimeException failure) {
                    semaphore.release();
                    result.completeExceptionally(failure);
                }
            });
        });
        return result;
    }

    private void afterAcquire(CompletableFuture<RentedNexusClient<TProxy>> result, CancellationToken token) {
        if (closed) {
            semaphore.release();
            result.completeExceptionally(new IllegalStateException("The pool is closed."));
            return;
        }
        while (true) {
            PooledClient<TProxy> candidate;
            synchronized (lock) {
                candidate = idle.pollFirst();
            }
            if (candidate == null) {
                break;
            }
            if (candidate.isHealthy()) {
                candidate.lastUsedNanos = timers.nanoTime();
                result.complete(new Rented(candidate));
                return;
            }
            closeClient(candidate);
        }

        NexusClient<TProxy> client = clientFactory.get();
        if (client == null) {
            throw new IllegalStateException("The client factory returned null.");
        }
        PooledClient<TProxy> pooled = new PooledClient<>(client, timers.nanoTime());
        PoolCounters.POOLED_CLIENTS.incrementAndGet();
        client.tryConnect(token).whenComplete((connection, error) -> {
            ConnectionResult outcome = connection != null ? connection
                : new ConnectionResult(ConnectionResult.Status.EXCEPTION,
                    DisconnectReason.SOCKET_ERROR, error);
            if (!outcome.isSuccess()) {
                closeClient(pooled);
                semaphore.release();
                log(NexusLogLevel.ERROR, "Failed to connect after renting.", outcome.exception());
                if (outcome.status() == ConnectionResult.Status.CANCELED) {
                    result.completeExceptionally(outcome.exception() != null ? outcome.exception()
                        : new CancellationException("The rent was canceled."));
                } else {
                    result.completeExceptionally(
                        new NexusClientPoolConnectionException("Failed to connect after renting.", outcome));
                }
                return;
            }
            if (closed) {
                closeClient(pooled);
                semaphore.release();
                result.completeExceptionally(new IllegalStateException("The pool is closed."));
                return;
            }
            if (!result.complete(new Rented(pooled))) {
                returnClient(pooled);
            }
        });
    }

    /** Returns a rented client: back to the idle queue when healthy, closed otherwise. */
    private void returnClient(PooledClient<TProxy> pooled) {
        boolean keep = false;
        if (!closed && pooled.isHealthy()) {
            pooled.lastUsedNanos = timers.nanoTime();
            synchronized (lock) {
                if (!closed) {
                    idle.addLast(pooled);
                    keep = true;
                }
            }
        }
        if (!keep) {
            closeClient(pooled);
        }
        semaphore.release();
    }

    /** Closes a pooled client once, off the calling thread. */
    private void closeClient(PooledClient<TProxy> pooled) {
        if (!pooled.closed.compareAndSet(false, true)) {
            return;
        }
        NexusExecutors.execute(executor, () -> closeNow(pooled));
    }

    private static void closeNow(PooledClient<?> pooled) {
        try {
            pooled.client.close();
        } finally {
            PoolCounters.POOLED_CLIENTS.decrementAndGet();
        }
    }

    private void onHealthCheckTimer() {
        synchronized (lock) {
            if (closed) {
                return;
            }
            healthCheck = null;
        }
        NexusExecutors.execute(executor, () -> {
            try {
                checkHealth();
            } finally {
                synchronized (lock) {
                    if (!closed) {
                        healthCheck = timers.schedule(this::onHealthCheckTimer, healthCheckIntervalMillis);
                    }
                }
            }
        });
    }

    /**
     * Runs the health and idle check now: closes idle clients that lost their connection, and idle clients beyond
     * {@code minIdleConnections} idle longer than {@code maxIdleTime}. It also runs on its own timer.
     */
    public void checkHealth() {
        if (closed) {
            return;
        }
        long now = timers.nanoTime();
        long maxIdleNanos = TimeUnit.MILLISECONDS.toNanos(config.maxIdleTimeMillis());
        int minRequired = config.minIdleConnections();
        List<PooledClient<TProxy>> evicted = new ArrayList<>();
        synchronized (lock) {
            int healthy = 0;
            for (Iterator<PooledClient<TProxy>> it = idle.iterator(); it.hasNext(); ) {
                PooledClient<TProxy> client = it.next();
                if (!client.isHealthy()) {
                    it.remove();
                    evicted.add(client);
                    continue;
                }
                if (now - client.lastUsedNanos < maxIdleNanos || healthy < minRequired) {
                    healthy++;
                } else {
                    it.remove();
                    evicted.add(client);
                }
            }
        }
        for (PooledClient<TProxy> client : evicted) {
            closeClient(client);
        }
    }

    /**
     * Gives access to a collection through a rented client (C# {@code GetCollectionConnector}). Each
     * {@link NexusCollectionConnector#collection()} returns the client the connector holds to the pool, then rents one,
     * so the connector holds at most one rental.
     *
     * @param collectionSelector selects the collection from the server proxy
     * @param <C> the collection type
     * @return the connector; close it to return the client
     */
    public <C> NexusCollectionConnector<C> collectionConnector(Function<TProxy, C> collectionSelector) {
        if (collectionSelector == null) {
            throw new NullPointerException("collectionSelector");
        }
        return new NexusCollectionConnector<C>() {
            private final AtomicReference<@Nullable RentedNexusClient<TProxy>> rental = new AtomicReference<>();

            @Override
            public CompletableFuture<C> collection() {
                // Return the earlier rental before renting again: holding it while waiting for a permit would block
                // for good on a pool of one connection.
                RentedNexusClient<TProxy> previous = rental.getAndSet(null);
                if (previous != null) {
                    previous.close();
                }
                return rent().thenApply(rented -> {
                    // A concurrent call may have stored its rental meanwhile: the connector holds the latest one only.
                    RentedNexusClient<TProxy> raced = rental.getAndSet(rented);
                    if (raced != null) {
                        raced.close();
                    }
                    return collectionSelector.apply(rented.proxy());
                });
            }

            @Override
            public void close() {
                RentedNexusClient<TProxy> rented = rental.getAndSet(null);
                if (rented != null) {
                    rented.close();
                }
            }
        };
    }

    /**
     * The maximum number of connections.
     *
     * @return the maximum
     */
    public int maxConnections() {
        return config.maxConnections();
    }

    /**
     * The number of idle connections.
     *
     * @return the number
     */
    public int availableConnections() {
        synchronized (lock) {
            return idle.size();
        }
    }

    /**
     * Whether {@link #close()} was called.
     *
     * @return true once closed
     */
    public boolean isClosed() {
        return closed;
    }

    /**
     * Closes the pool: stops the health check, fails waiting rents, and closes every idle client, waiting a bounded
     * time for each. Rented clients are closed when they are returned. Idempotent.
     */
    @Override
    public void close() {
        Registration timer;
        List<PooledClient<TProxy>> drained;
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            timer = healthCheck;
            healthCheck = null;
            drained = new ArrayList<>(idle);
            idle.clear();
        }
        if (timer != null) {
            timer.close();
        }
        PoolCounters.HEALTH_CHECKS.decrementAndGet();
        semaphore.close();
        for (PooledClient<TProxy> pooled : drained) {
            if (pooled.closed.compareAndSet(false, true)) {
                closeNow(pooled);
            }
        }
    }

    private void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Pool", exception, message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the pool.
            }
        }
    }

    /** A rental: returns the client once when closed. */
    private final class Rented implements RentedNexusClient<TProxy> {
        private final AtomicReference<@Nullable PooledClient<TProxy>> pooled;

        Rented(PooledClient<TProxy> pooled) {
            this.pooled = new AtomicReference<>(pooled);
        }

        private PooledClient<TProxy> require() {
            PooledClient<TProxy> current = pooled.get();
            if (current == null) {
                throw new IllegalStateException("The client was returned to the pool.");
            }
            return current;
        }

        @Override
        public TProxy proxy() {
            return require().client.proxy();
        }

        @Override
        public ConnectionState state() {
            PooledClient<TProxy> current = pooled.get();
            return current == null ? ConnectionState.DISCONNECTED : current.client.state();
        }

        @Override
        public CompletableFuture<Void> disconnected() {
            PooledClient<TProxy> current = pooled.get();
            return current == null ? CompletableFuture.completedFuture(null) : current.client.disconnected();
        }

        @Override
        public CompletableFuture<Boolean> ensureConnected() {
            PooledClient<TProxy> current = pooled.get();
            if (current == null) {
                return CompletableFuture.completedFuture(false);
            }
            if (current.client.state() == ConnectionState.CONNECTED) {
                return CompletableFuture.completedFuture(true);
            }
            return current.client.tryConnect().thenApply(ConnectionResult::isSuccess);
        }

        @Override
        public void close() {
            PooledClient<TProxy> current = pooled.getAndSet(null);
            if (current != null) {
                returnClient(current);
            }
        }
    }
}
