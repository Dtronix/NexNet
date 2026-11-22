package com.dtronix.nexnet.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.diagnostics.TestClients;
import com.dtronix.nexnet.diagnostics.TestClients.TestProxy;
import com.dtronix.nexnet.reconnect.DefaultReconnectionPolicy;
import com.dtronix.nexnet.reconnect.ReconnectController;
import com.dtronix.nexnet.reconnect.ReconnectListener;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class NexusClientReconnectTest {
    private static ClientConfig config() {
        return ClientConfig.tcp("h", 1).reconnectionPolicy(new DefaultReconnectionPolicy(new long[] {0, 50}, true))
            .build();
    }

    @Test
    void reconnectsAfterALostSessionAndDisconnectedStaysPending() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        try {
            ReconnectController controller = client.reconnection();
            assertNotNull(controller);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            client.connect().get(5, TimeUnit.SECONDS);
            CompletableFuture<Void> disconnected = client.disconnected();

            server.disconnect(0, 30);
            TestClients.waitFor(() -> server.transports.size() == 2 && client.state() == ConnectionState.CONNECTED);
            controller.idle().get(5, TimeUnit.SECONDS);
            assertEquals(1, controller.attempts());
            assertFalse(disconnected.isDone());
            TestClients.waitFor(() -> states.contains(ConnectionState.RECONNECTING));

            // A user disconnect does not reconnect, and completes both futures.
            client.disconnect().get(5, TimeUnit.SECONDS);
            disconnected.get(5, TimeUnit.SECONDS);
            Thread.sleep(100);
            assertEquals(ConnectionState.DISCONNECTED, client.state());
            assertEquals(2, server.transports.size());
            assertFalse(controller.isReconnecting());
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void doesNotReconnectForAGracefulDisconnectOrWithoutAPolicy() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        NexusClient<TestProxy> plain = TestClients.client(server);
        try {
            assertNull(plain.reconnection());
            client.connect().get(5, TimeUnit.SECONDS);
            server.disconnect(0, 21);
            client.disconnected().get(5, TimeUnit.SECONDS);
            Thread.sleep(100);
            assertEquals(1, server.transports.size());
            assertEquals(ConnectionState.DISCONNECTED, client.state());

            plain.connect().get(5, TimeUnit.SECONDS);
            server.disconnect(1, 30);
            plain.disconnected().get(5, TimeUnit.SECONDS);
            Thread.sleep(100);
            assertEquals(2, server.transports.size());
        } finally {
            client.close();
            plain.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void reportsDisconnectingAndDisconnectedBeforeReconnecting() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        try {
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> states.contains(ConnectionState.CONNECTED));

            // C# and JS: the lost session reports Disconnecting and Disconnected, then the loop Reconnecting (K54).
            server.disconnect(0, 30);
            TestClients.waitFor(() -> server.transports.size() == 2 && states.size() >= 6);
            Thread.sleep(100);
            assertEquals(List.of(ConnectionState.CONNECTING, ConnectionState.CONNECTED,
                ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED,
                ConnectionState.RECONNECTING, ConnectionState.CONNECTED), states);
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void doesNotReportReconnectingWhenThePolicyDeclinesTheReason() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        try {
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> states.contains(ConnectionState.CONNECTED));

            // C#: the policy is asked before the state changes; a declined reason (graceful, 21) never reports
            // Reconnecting (K55).
            server.disconnect(0, 21);
            client.disconnected().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> states.contains(ConnectionState.DISCONNECTED));
            Thread.sleep(100);
            assertEquals(List.of(ConnectionState.CONNECTING, ConnectionState.CONNECTED,
                ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED), states);
            assertEquals(ConnectionState.DISCONNECTED, client.state());
            assertEquals(1, server.transports.size());
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void reportsDisconnectingAndDisconnectedForALostSessionWithoutAPolicy() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(server);
        try {
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> client.state() == ConnectionState.CONNECTED);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            server.disconnect(0, 30);
            client.disconnected().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> states.contains(ConnectionState.DISCONNECTED));
            Thread.sleep(100);
            assertEquals(List.of(ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED), states);
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void staysReconnectingBetweenFailedAttempts() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        try {
            ReconnectController controller = client.reconnection();
            assertNotNull(controller);
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> client.state() == ConnectionState.CONNECTED);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);

            // C#: Reconnecting from the loss of the session until an attempt connects, also between failed attempts.
            server.refuse = true;
            server.disconnect(0, 30);
            TestClients.waitFor(() -> controller.attempts() >= 3);
            assertEquals(ConnectionState.RECONNECTING, client.state());
            server.refuse = false;
            TestClients.waitFor(() -> client.state() == ConnectionState.CONNECTED);
            TestClients.waitFor(() -> states.contains(ConnectionState.CONNECTED));
            assertEquals(List.of(ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED,
                ConnectionState.RECONNECTING, ConnectionState.CONNECTED), states);
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void endsDisconnectedWhenTheUserDisconnectsDuringTheLoop() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(ClientConfig.tcp("h", 1)
            .reconnectionPolicy(new DefaultReconnectionPolicy(new long[] {0, 60_000}, true)).build(), server);
        try {
            ReconnectController controller = client.reconnection();
            assertNotNull(controller);
            client.connect().get(5, TimeUnit.SECONDS);
            server.refuse = true;
            server.disconnect(0, 30);
            TestClients.waitFor(() -> controller.attempts() >= 1 && controller.isWaiting());
            assertEquals(ConnectionState.RECONNECTING, client.state());
            client.disconnect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> client.state() == ConnectionState.DISCONNECTED);
            assertFalse(controller.isReconnecting());
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void givesUpWhenThePolicyStops() throws Exception {
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(ClientConfig.tcp("h", 1)
            .reconnectionPolicy(new DefaultReconnectionPolicy(new long[] {0, 10}, false)).build(), server);
        try {
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> client.state() == ConnectionState.CONNECTED);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            CompletableFuture<Void> disconnected = client.disconnected();
            server.refuse = true;
            server.disconnect(0, 20);
            disconnected.get(5, TimeUnit.SECONDS);
            ReconnectController controller = client.reconnection();
            assertNotNull(controller);
            assertEquals(2, controller.attempts());
            assertTrue(controller.idle().isDone());
            TestClients.waitFor(() -> client.state() == ConnectionState.DISCONNECTED);
            // The lost session's own states, Reconnecting through both failed attempts, then Disconnected once when
            // the loop gives up (C# parity).
            TestClients.waitFor(() -> states.size() >= 4);
            Thread.sleep(100);
            assertEquals(List.of(ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED,
                ConnectionState.RECONNECTING, ConnectionState.DISCONNECTED), states);
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }

    @Test
    void aReconnectedSessionLostBeforeTheLoopEndsIsReconnected() throws Exception {
        // K56 (C# S49): the attempt connected and its session is lost before the loop has finished (here from the
        // onReconnected listener, which runs before the loop ends). Before, the loss found the loop still running,
        // started nothing, and the client stayed DISCONNECTED.
        TestClients.Server server = new TestClients.Server();
        NexusClient<TestProxy> client = TestClients.client(config(), server);
        try {
            ReconnectController controller = client.reconnection();
            assertNotNull(controller);
            client.connect().get(5, TimeUnit.SECONDS);
            TestClients.waitFor(() -> client.state() == ConnectionState.CONNECTED);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            client.addStateListener(states::add);
            CompletableFuture<Void> disconnected = client.disconnected();

            CountDownLatch handedOver = new CountDownLatch(1);
            AtomicBoolean once = new AtomicBoolean();
            controller.addListener(new ReconnectListener() {
                @Override
                public void onReconnected(int attempts) {
                    if (!once.compareAndSet(false, true)) {
                        return;
                    }
                    client.afterSessionLost = handedOver::countDown;
                    server.disconnect(1, 30);
                    try {
                        // Hold the loop open until the client has reported the loss to the controller.
                        assertTrue(handedOver.await(5, TimeUnit.SECONDS));
                    } catch (InterruptedException e) {
                        Thread.currentThread().interrupt();
                    }
                }
            });

            server.disconnect(0, 30);
            TestClients.waitFor(() -> server.transports.size() == 3 && client.state() == ConnectionState.CONNECTED);
            controller.idle().get(5, TimeUnit.SECONDS);
            assertFalse(disconnected.isDone());
            TestClients.waitFor(() -> states.size() >= 8);
            Thread.sleep(100);
            assertEquals(List.of(ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED,
                ConnectionState.RECONNECTING, ConnectionState.CONNECTED,
                ConnectionState.DISCONNECTING, ConnectionState.DISCONNECTED,
                ConnectionState.RECONNECTING, ConnectionState.CONNECTED), states);

            client.disconnect().get(5, TimeUnit.SECONDS);
            disconnected.get(5, TimeUnit.SECONDS);
            assertEquals(ConnectionState.DISCONNECTED, client.state());
        } finally {
            client.close();
        }
        TestClients.awaitIdle();
    }
}
