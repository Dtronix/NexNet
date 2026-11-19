package com.dtronix.nexnet.client;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.GoldenFrames;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionCounters;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.Transport;
import java.net.ConnectException;
import java.net.URI;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class NexusClientTest {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {0});

    static final class TestProxy extends ProxyBase {
        TestProxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class TestNexus extends ClientNexusBase<TestProxy> {
        final List<String> events = new CopyOnWriteArrayList<>();

        @Override
        protected CompletableFuture<Void> onConnected(boolean isReconnected) {
            events.add("connected:" + isReconnected + ":" + Thread.currentThread().getName());
            return CompletableFuture.completedFuture(null);
        }

        @Override
        protected CompletableFuture<Void> onDisconnected() {
            events.add("disconnected");
            return CompletableFuture.completedFuture(null);
        }

        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    static final class RecordingLogger implements NexusLogger {
        final List<String> warnings = new CopyOnWriteArrayList<>();

        @Override
        public void log(NexusLogLevel level, String category, Throwable exception, String message) {
            if (level == NexusLogLevel.WARNING) {
                warnings.add(message);
            }
        }
    }

    /** A connector handing out loopback transports, with a server thread that accepts each connection. */
    final class Server implements Transport.Connector {
        final List<LoopbackTransport> transports = new CopyOnWriteArrayList<>();
        final List<String> connectThreads = new CopyOnWriteArrayList<>();
        final List<byte[]> greetings = new CopyOnWriteArrayList<>();
        volatile byte[] reply;
        volatile boolean secure;

        Server(byte[] reply) {
            this.reply = reply;
        }

        @Override
        public Transport connect(ClientConfig config, int timeoutMillis) {
            connectThreads.add(Thread.currentThread().getName());
            LoopbackTransport transport = new LoopbackTransport(secure);
            transports.add(transport);
            byte[] answer = reply;
            Thread thread = new Thread(() -> {
                try {
                    greetings.add(transport.server().readHandshake());
                    if (answer != null) {
                        transport.server().write(answer);
                    }
                } catch (RuntimeException ignored) {
                    // The client closed first.
                }
            }, "test-server");
            thread.setDaemon(true);
            thread.start();
            return transport;
        }

        LoopbackTransport last() {
            return transports.get(transports.size() - 1);
        }
    }

    private final List<NexusClient<TestProxy>> clients = new ArrayList<>();
    private final TestNexus nexus = new TestNexus();

    static byte[] accept() {
        return concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting"));
    }

    private NexusClient<TestProxy> client(ClientConfig config, Transport.Connector connector) {
        NexusClient<TestProxy> client = NexusClient.create(config, HANDSHAKE, nexus, TestProxy::new, connector,
            new ManualTimers(), SessionOptions.defaults().withDisconnectDelayMillis(0));
        clients.add(client);
        return client;
    }

    @AfterEach
    void tearDown() {
        for (NexusClient<TestProxy> client : clients) {
            client.close();
        }
    }

    @Test
    void connectsOnARuntimeThreadAndCompletesOnTheCallbackExecutor() throws Exception {
        Server server = new Server(accept());
        AtomicReference<Thread> callbackThread = new AtomicReference<>();
        ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> {
            Thread thread = new Thread(r, "test-callbacks");
            callbackThread.set(thread);
            return thread;
        });
        try {
            NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).callbackExecutor(callbacks).build(), server);
            List<ConnectionState> states = new CopyOnWriteArrayList<>();
            List<String> listenerThreads = new CopyOnWriteArrayList<>();
            client.addStateListener(state -> {
                states.add(state);
                listenerThreads.add(Thread.currentThread().getName());
            });
            assertEquals(ConnectionState.DISCONNECTED, client.state());
            // Hold the callback executor until thenRun is attached: a dependent attached to an already-completed
            // future runs on the attaching thread, so a fast connect would run it on this test thread.
            CountDownLatch attached = hold(callbacks);
            AtomicReference<Thread> completedOn = new AtomicReference<>();
            CompletableFuture<Void> connected = client.connect().thenRun(() -> completedOn.set(Thread.currentThread()));
            attached.countDown();
            connected.get(5, TimeUnit.SECONDS);

            assertSame(callbackThread.get(), completedOn.get());
            assertTrue(server.connectThreads.get(0).startsWith("nexnet-pool-"), server.connectThreads.get(0));
            assertNotEquals(Thread.currentThread().getName(), server.connectThreads.get(0));
            assertEquals(ConnectionState.CONNECTED, client.state());
            waitFor(() -> states.size() == 2);
            assertEquals(List.of(ConnectionState.CONNECTING, ConnectionState.CONNECTED), states);
            assertTrue(listenerThreads.stream().allMatch("test-callbacks"::equals));
            assertEquals(42, nexus.context().id());
            assertSame(client.proxy(), nexus.context().proxy());
            assertFalse(nexus.context().isSecure());
            assertTrue(nexus.events.get(0).startsWith("connected:false:"));
            assertFalse(nexus.events.get(0).contains("reader"));
        } finally {
            callbacks.shutdownNow();
        }
    }

    @Test
    void sendsTheTokenFromTheSupplierInTheGreeting() throws Exception {
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1)
            .authenticate(() -> new byte[] {1, 2, 3}).build(), server);
        client.connect().get(5, TimeUnit.SECONDS);
        assertArrayEquals(GoldenFrames.get("client-greeting-v2"), server.greetings.get(0));
    }

    @Test
    void authenticationRunsBeforeTheTransportOnEveryConnect() throws Exception {
        Server server = new Server(accept());
        List<String> order = new CopyOnWriteArrayList<>();
        Transport.Connector connector = (config, timeout) -> {
            order.add("connect");
            return server.connect(config, timeout);
        };
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1)
            .authenticateAsync(() -> {
                order.add("auth");
                return CompletableFuture.completedFuture(new byte[] {1, 2, 3});
            }).build(), connector);
        client.connect().get(5, TimeUnit.SECONDS);
        client.disconnect().get(5, TimeUnit.SECONDS);
        client.connect().get(5, TimeUnit.SECONDS);
        assertEquals(List.of("auth", "connect", "auth", "connect"), order);
    }

    @Test
    void aFailingAuthenticateSupplierOpensNoConnection() throws Exception {
        Server server = new Server(accept());
        IllegalStateException failure = new IllegalStateException("no token");
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1)
            .authenticate(() -> {
                throw failure;
            }).build(), server);
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionResult.Status.EXCEPTION, result.status());
        assertEquals(DisconnectReason.AUTHENTICATION, result.disconnectReason());
        assertSame(failure, result.exception());
        assertTrue(server.transports.isEmpty());
        ExecutionException e = assertThrows(ExecutionException.class, () -> client.connect().get(5, TimeUnit.SECONDS));
        assertSame(failure, e.getCause());
        assertEquals(ConnectionState.DISCONNECTED, client.state());
    }

    @Test
    void aSlowAsyncAuthenticateSupplierTimesOutWithTheHandshakeTimeout() throws Exception {
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).handshakeTimeoutMillis(100)
            .authenticateAsync(CompletableFuture::new).build(), server);
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(DisconnectReason.AUTHENTICATION, result.disconnectReason());
        NexusTransportException e = assertInstanceOf(NexusTransportException.class, result.exception());
        assertEquals(TransportError.AUTHENTICATION_ERROR, e.error());
        assertTrue(server.transports.isEmpty());
    }

    @Test
    void logsThePlaintextWarningOncePerClient() throws Exception {
        RecordingLogger logger = new RecordingLogger();
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).logger(logger).build(), server);
        assertFalse(client.isSecure());
        client.connect().get(5, TimeUnit.SECONDS);
        client.disconnect().get(5, TimeUnit.SECONDS);
        client.connect().get(5, TimeUnit.SECONDS);
        assertEquals(List.of("NexNet: unencrypted transport (tcp); credentials and data are visible on the network."),
            logger.warnings);

        RecordingLogger wsLogger = new RecordingLogger();
        NexusClient<TestProxy> ws = client(ClientConfig.webSocket(URI.create("ws://h/nexus")).logger(wsLogger).build(),
            new Server(accept()));
        ws.connect().get(5, TimeUnit.SECONDS);
        assertEquals(List.of("NexNet: unencrypted transport (ws://); credentials and data are visible on the network."),
            wsLogger.warnings);
    }

    @Test
    void secureTransportsLogNoWarning() throws Exception {
        RecordingLogger logger = new RecordingLogger();
        Server server = new Server(accept());
        server.secure = true;
        NexusClient<TestProxy> client = client(ClientConfig.tcpTls("h", 1).logger(logger).build(), server);
        assertTrue(client.isSecure());
        client.connect().get(5, TimeUnit.SECONDS);
        assertTrue(logger.warnings.isEmpty());
        assertTrue(nexus.context().isSecure());
    }

    @Test
    void aClientMismatchIsReportedWithItsReason() throws Exception {
        Server server = new Server(concat(FakeServer.PREAMBLE,
            FakeServer.serverGreeting(CLIENT_HASH + 1, 1, 65_526, 1)));
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), server);
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionResult.Status.DISCONNECTED, result.status());
        assertEquals(DisconnectReason.CLIENT_MISMATCH, result.disconnectReason());
        assertEquals(ConnectionState.DISCONNECTED, client.state());
        assertTrue(client.disconnected().isDone());

        ExecutionException e = assertThrows(ExecutionException.class, () -> client.connect().get(5, TimeUnit.SECONDS));
        assertInstanceOf(NexusException.class, e.getCause());
        assertTrue(e.getCause().getMessage().contains("CLIENT_MISMATCH"));
    }

    @Test
    void anAuthenticationDisconnectFailsConnectWithAnAuthenticationError() throws Exception {
        Server server = new Server(concat(FakeServer.PREAMBLE, new byte[] {29}));
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), server);
        ExecutionException e = assertThrows(ExecutionException.class, () -> client.connect().get(5, TimeUnit.SECONDS));
        NexusTransportException transport = assertInstanceOf(NexusTransportException.class, e.getCause());
        assertEquals(TransportError.AUTHENTICATION_ERROR, transport.error());
    }

    @Test
    void transportFailuresAreMapped() throws Exception {
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), (config, timeout) -> {
            throw new ConnectException("refused");
        });
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionResult.Status.DISCONNECTED, result.status());
        assertEquals(DisconnectReason.SOCKET_ERROR, result.disconnectReason());
        assertEquals(TransportError.CONNECTION_REFUSED,
            assertInstanceOf(NexusTransportException.class, result.exception()).error());
    }

    @Test
    void theDefaultConnectorReportsAMissingTransport() throws Exception {
        NexusClient<TestProxy> client = NexusClient.create(ClientConfig.tcp("h", 1).build(), HANDSHAKE, nexus,
            TestProxy::new);
        clients.add(client);
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionResult.Status.DISCONNECTED, result.status());
        assertInstanceOf(NexusTransportException.class, result.exception());
    }

    @Test
    void connectionTimeoutCoversTheHandshake() throws Exception {
        Server server = new Server(null);
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).connectionTimeoutMillis(200).build(), server);
        ConnectionResult result = client.tryConnect().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionResult.Status.EXCEPTION, result.status());
        assertEquals(DisconnectReason.TIMEOUT, result.disconnectReason());
        assertEquals(TransportError.CONNECTION_TIMEOUT,
            assertInstanceOf(NexusTransportException.class, result.exception()).error());
        assertEquals(DisconnectReason.TIMEOUT, server.last().closed.get(5, TimeUnit.SECONDS));
    }

    @Test
    void cancelingCompletesTheAttemptAtOnce() throws Exception {
        CountDownLatch blocked = new CountDownLatch(1);
        CountDownLatch release = new CountDownLatch(1);
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), (config, timeout) -> {
            blocked.countDown();
            try {
                release.await();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            return new LoopbackTransport();
        });
        try (CancellationTokenSource cts = new CancellationTokenSource()) {
            CompletableFuture<Void> connect = client.connect(cts.token());
            assertTrue(blocked.await(5, TimeUnit.SECONDS));
            cts.cancel();
            // A future failed with CancellationException reports itself canceled.
            assertThrows(CancellationException.class, () -> connect.get(5, TimeUnit.SECONDS));
            assertTrue(connect.isCancelled());
            waitFor(() -> client.state() == ConnectionState.DISCONNECTED);
        } finally {
            release.countDown();
        }
    }

    @Test
    void connectingTwiceFails() throws Exception {
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), new Server(accept()));
        client.connect().get(5, TimeUnit.SECONDS);
        ExecutionException e = assertThrows(ExecutionException.class, () -> client.connect().get(5, TimeUnit.SECONDS));
        assertInstanceOf(IllegalStateException.class, e.getCause());
        assertEquals(ConnectionResult.Status.EXCEPTION, client.tryConnect().get(5, TimeUnit.SECONDS).status());
    }

    @Test
    void disconnectSendsGracefulAndRunsTheHook() throws Exception {
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), server);
        client.connect().get(5, TimeUnit.SECONDS);
        CompletableFuture<Void> disconnected = client.disconnected();
        assertFalse(disconnected.isDone());
        client.disconnect().get(5, TimeUnit.SECONDS);
        assertTrue(disconnected.isDone());
        assertEquals(21, server.last().server().readByte());
        assertEquals(ConnectionState.DISCONNECTED, client.state());
        assertEquals("disconnected", nexus.events.get(nexus.events.size() - 1));
        assertNull(client.session());
        assertThrows(IllegalStateException.class, nexus::context);
    }

    @Test
    void aServerDisconnectIsObserved() throws Exception {
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), server);
        client.connect().get(5, TimeUnit.SECONDS);
        server.last().server().write(new byte[] {28});
        client.disconnected().get(5, TimeUnit.SECONDS);
        assertEquals(ConnectionState.DISCONNECTED, client.state());
        server.last().server().expectEnd();
    }

    @Test
    void userCodeBlockingInThenApplyDoesNotStallTheReader() throws Exception {
        // Deadlock guard: the connect future completes on the callback executor, so a continuation that blocks
        // holds a callback thread, never the reader.
        ExecutorService callbacks = Executors.newSingleThreadExecutor();
        CountDownLatch release = new CountDownLatch(1);
        try {
            Server server = new Server(accept());
            NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).callbackExecutor(callbacks).build(),
                server);
            AtomicInteger ran = new AtomicInteger();
            // Hold the callback executor until thenApply is attached: if connect completed first, the blocking
            // continuation would run on this test thread and the test would hang until its timeout.
            CountDownLatch attached = hold(callbacks);
            CompletableFuture<Integer> blocked = client.connect().thenApply(v -> {
                ran.incrementAndGet();
                try {
                    release.await();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                }
                return 1;
            });
            attached.countDown();
            waitFor(() -> ran.get() == 1);
            NexusSession session = client.session();
            server.last().server().write(new byte[] {28});
            assertEquals(DisconnectReason.SERVER_SHUTDOWN, session.closedInternal().get(5, TimeUnit.SECONDS));
            assertTrue(session.awaitThreads());
            assertFalse(blocked.isDone());
            release.countDown();
            assertEquals(1, blocked.get(5, TimeUnit.SECONDS));
        } finally {
            release.countDown();
            callbacks.shutdownNow();
        }
    }

    @Test
    void closeEndsTheThreadsAndLaterConnectsFail() throws Exception {
        int threads = SessionCounters.liveThreads();
        Server server = new Server(accept());
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).build(), server);
        client.connect().get(5, TimeUnit.SECONDS);
        NexusSession session = client.session();
        client.close();
        assertTrue(session.awaitThreads());
        assertEquals(threads, SessionCounters.liveThreads());
        client.close();
        ExecutionException e = assertThrows(ExecutionException.class, () -> client.connect().get(5, TimeUnit.SECONDS));
        assertInstanceOf(IllegalStateException.class, e.getCause());
    }

    @Test
    void refusesGeneratedCodeForAnotherMajorMinor() {
        HandshakeDescriptor other = new HandshakeDescriptor("999.0.0", null, 1, 2, new int[0]);
        assertThrows(IllegalStateException.class,
            () -> NexusClient.create(ClientConfig.tcp("h", 1).build(), other, nexus, TestProxy::new));
        assertTrue(NexusClient.sameMajorMinor("0.20.0", "0.20.3"));
        assertTrue(NexusClient.sameMajorMinor("0.20.0-beta.1", "0.20.1"));
        assertFalse(NexusClient.sameMajorMinor("0.21.0", "0.20.0"));
    }

    /** Occupies a single-thread executor until the returned latch is counted down. */
    static CountDownLatch hold(ExecutorService executor) throws InterruptedException {
        CountDownLatch running = new CountDownLatch(1);
        CountDownLatch release = new CountDownLatch(1);
        executor.execute(() -> {
            running.countDown();
            try {
                release.await();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        });
        assertTrue(running.await(5, TimeUnit.SECONDS));
        return release;
    }

    static void waitFor(java.util.function.BooleanSupplier condition) throws InterruptedException {
        long deadline = System.currentTimeMillis() + 5_000;
        while (!condition.getAsBoolean()) {
            if (System.currentTimeMillis() > deadline) {
                throw new AssertionError("Condition not met.");
            }
            Thread.sleep(2);
        }
    }

    static byte[] concat(byte[] a, byte[] b) {
        byte[] c = new byte[a.length + b.length];
        System.arraycopy(a, 0, c, 0, a.length);
        System.arraycopy(b, 0, c, a.length, b.length);
        return c;
    }
}
