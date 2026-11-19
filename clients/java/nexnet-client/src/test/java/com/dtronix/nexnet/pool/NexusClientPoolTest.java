package com.dtronix.nexnet.pool;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotSame;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.diagnostics.Diagnostics;
import com.dtronix.nexnet.diagnostics.TestClients;
import com.dtronix.nexnet.diagnostics.TestClients.TestProxy;
import com.dtronix.nexnet.session.ManualTimers;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(60)
class NexusClientPoolTest {
    private final TestClients.Server server = new TestClients.Server();
    private final ManualTimers timers = new ManualTimers();
    private NexusClientPool<TestProxy> pool;

    private NexusClientPool<TestProxy> pool(int max, long maxIdleMillis, int minIdle) {
        NexusClientPoolConfig config = new NexusClientPoolConfig(ClientConfig.tcp("h", 1).build())
            .maxConnections(max).maxIdleTimeMillis(maxIdleMillis).minIdleConnections(minIdle);
        pool = new NexusClientPool<>(config, () -> TestClients.client(server), timers);
        return pool;
    }

    private static <T> T get(CompletableFuture<T> future) throws Exception {
        return future.get(10, TimeUnit.SECONDS);
    }

    @AfterEach
    void closeAndAudit() throws Exception {
        if (pool != null) {
            pool.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void rentsAConnectedClientAndReusesItOnceReturned() throws Exception {
        NexusClientPool<TestProxy> p = pool(2, 60_000, 1);
        assertEquals(2, p.maxConnections());
        assertEquals(0, p.availableConnections());
        RentedNexusClient<TestProxy> first = get(p.rent());
        assertEquals(ConnectionState.CONNECTED, first.state());
        assertTrue(get(first.ensureConnected()));
        assertFalse(first.disconnected().isDone());
        TestProxy proxy = first.proxy();
        assertEquals(1, PoolCounters.pooledClients());

        first.close();
        first.close();
        assertEquals(1, p.availableConnections());
        assertThrows(IllegalStateException.class, first::proxy);
        assertEquals(ConnectionState.DISCONNECTED, first.state());
        assertFalse(get(first.ensureConnected()));

        RentedNexusClient<TestProxy> second = get(p.rent());
        assertSame(proxy, second.proxy());
        assertEquals(1, server.transports.size());
        second.close();
    }

    @Test
    void rentsWaitForAFreeSlotInOrder() throws Exception {
        NexusClientPool<TestProxy> p = pool(1, 60_000, 1);
        RentedNexusClient<TestProxy> held = get(p.rent());
        CompletableFuture<RentedNexusClient<TestProxy>> second = p.rent();
        CompletableFuture<RentedNexusClient<TestProxy>> third = p.rent();
        Thread.sleep(50);
        assertFalse(second.isDone());
        assertEquals(2, PoolCounters.pendingRents());
        TestProxy proxy = held.proxy();
        held.close();
        RentedNexusClient<TestProxy> next = get(second);
        assertSame(proxy, next.proxy());
        assertFalse(third.isDone());
        next.close();
        get(third).close();
        assertEquals(0, PoolCounters.pendingRents());
    }

    @Test
    void aWaitingRentCanBeCanceled() throws Exception {
        NexusClientPool<TestProxy> p = pool(1, 60_000, 1);
        RentedNexusClient<TestProxy> held = get(p.rent());
        CancellationTokenSource cts = new CancellationTokenSource();
        CompletableFuture<RentedNexusClient<TestProxy>> waiting = p.rent(cts.token());
        cts.cancel();
        assertThrows(CancellationException.class, () -> get(waiting));
        assertTrue(waiting.isCompletedExceptionally());
        assertEquals(0, PoolCounters.pendingRents());
        held.close();
        get(p.rent()).close();
    }

    @Test
    void aFailedConnectionFailsTheRentAndFreesTheSlot() throws Exception {
        NexusClientPool<TestProxy> p = pool(1, 60_000, 1);
        server.refuse = true;
        ExecutionException e = assertThrows(ExecutionException.class, () -> get(p.rent()));
        NexusClientPoolConnectionException failure =
            assertInstanceOf(NexusClientPoolConnectionException.class, e.getCause());
        assertEquals(ConnectionResult.Status.DISCONNECTED, failure.result().status());
        server.refuse = false;
        get(p.rent()).close();
    }

    @Test
    void unhealthyIdleClientsAreReplaced() throws Exception {
        NexusClientPool<TestProxy> p = pool(2, 60_000, 1);
        RentedNexusClient<TestProxy> rented = get(p.rent());
        TestProxy proxy = rented.proxy();
        CompletableFuture<Void> lost = rented.disconnected();
        rented.close();
        server.disconnect(0, 28);
        get(lost);
        TestClients.waitFor(() -> server.transports.get(0).closed.isDone());
        RentedNexusClient<TestProxy> replacement = get(p.rent());
        assertNotSame(proxy, replacement.proxy());
        assertEquals(2, server.transports.size());
        replacement.close();
    }

    @Test
    void theHealthCheckEvictsDisconnectedAndLongIdleClientsBeyondTheMinimum() throws Exception {
        NexusClientPool<TestProxy> p = pool(3, 1_000, 1);
        RentedNexusClient<TestProxy> a = get(p.rent());
        RentedNexusClient<TestProxy> b = get(p.rent());
        RentedNexusClient<TestProxy> c = get(p.rent());
        a.close();
        b.close();
        c.close();
        assertEquals(3, p.availableConnections());

        // The health check runs every maxIdle / 4 (250 ms); nothing is idle long enough yet.
        timers.advance(250);
        TestClients.waitFor(() -> timers.pending() == 1);
        assertEquals(3, p.availableConnections());

        server.disconnect(0, 28);
        try {
            TestClients.waitFor(() -> {
                p.checkHealth();
                return p.availableConnections() == 2;
            });
        } catch (AssertionError e) {
            throw new AssertionError("available " + p.availableConnections() + ", transports "
                + server.transports.size() + ", closed " + server.transports.get(0).closed.isDone(), e);
        }

        timers.advance(1_000);
        TestClients.waitFor(() -> p.availableConnections() == 1);
        TestClients.waitFor(() -> PoolCounters.pooledClients() == 1);
    }

    @Test
    void closeClosesIdleClientsFailsWaitersAndClosesReturnedClients() throws Exception {
        NexusClientPool<TestProxy> p = pool(2, 60_000, 1);
        RentedNexusClient<TestProxy> idle = get(p.rent());
        RentedNexusClient<TestProxy> held = get(p.rent());
        idle.close();
        // This rent takes the idle client; the next one waits for a free slot.
        RentedNexusClient<TestProxy> third = get(p.rent());
        CompletableFuture<RentedNexusClient<TestProxy>> blocked = p.rent();
        assertEquals(1, PoolCounters.healthChecks());
        p.close();
        p.close();
        assertTrue(p.isClosed());
        ExecutionException e = assertThrows(ExecutionException.class, () -> get(blocked));
        assertInstanceOf(IllegalStateException.class, e.getCause());
        assertEquals(0, PoolCounters.healthChecks());
        assertEquals(0, timers.pending());

        third.close();
        held.close();
        TestClients.waitFor(() -> PoolCounters.pooledClients() == 0);
        assertThrows(ExecutionException.class, () -> get(p.rent()));
        assertTrue(Diagnostics.counters().pooledClients() == 0);
    }

    @Test
    void theCollectionConnectorRentsAndReturns() throws Exception {
        NexusClientPool<TestProxy> p = pool(1, 60_000, 1);
        NexusCollectionConnector<String> connector = p.collectionConnector(proxy -> "collection");
        assertEquals("collection", get(connector.collection()));
        assertEquals(0, p.availableConnections());
        connector.close();
        connector.close();
        assertEquals(1, p.availableConnections());
    }

    @Test
    void theConfigurationValidatesAndDefaultsLikeCSharp() {
        NexusClientPoolConfig config = new NexusClientPoolConfig(ClientConfig.tcp("h", 1).build());
        assertEquals(10, config.maxConnections());
        assertEquals(120_000, config.maxIdleTimeMillis());
        assertEquals(1, config.minIdleConnections());
        assertThrows(IllegalArgumentException.class, () -> config.maxConnections(0));
        assertThrows(IllegalArgumentException.class, () -> config.maxIdleTimeMillis(0));
        assertThrows(IllegalArgumentException.class, () -> config.minIdleConnections(-1));
        assertThrows(NullPointerException.class, () -> new NexusClientPool<TestProxy>(config, null));
    }
}
