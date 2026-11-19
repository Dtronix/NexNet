package com.dtronix.nexnet.session;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class NexusSessionTest {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor("0.20.0", "v2", 123456789, CLIENT_HASH, new int[] {0, 1});

    private final ManualTimers timers = new ManualTimers();
    private final List<NexusSession> sessions = new ArrayList<>();
    private LoopbackTransport transport = new LoopbackTransport();

    private NexusSession.Builder builder() {
        return NexusSession.builder(transport, HANDSHAKE).name("t").timers(timers)
            .options(SessionOptions.defaults().withDisconnectDelayMillis(0))
            .authenticationToken(new byte[] {1, 2, 3});
    }

    private NexusSession start(NexusSession.Builder builder) {
        NexusSession session = builder.build();
        sessions.add(session);
        session.start();
        return session;
    }

    private NexusSession connected() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(GoldenFrames.get("server-greeting"));
        session.ready().get(5, TimeUnit.SECONDS);
        return session;
    }

    private static DisconnectReason closedWith(NexusSession session) throws Exception {
        return session.closedInternal().get(5, TimeUnit.SECONDS);
    }

    @AfterEach
    void tearDown() throws Exception {
        for (NexusSession session : sessions) {
            session.abort(DisconnectReason.GRACEFUL);
            assertTrue(session.awaitThreads(), "threads ended");
        }
    }

    @Test
    void writesThePreambleAndTheGoldenGreeting() {
        start(builder());
        transport.server().expectPreamble();
        assertArrayEquals(GoldenFrames.get("client-greeting-v2"), transport.server().readFrame());
    }

    @Test
    void writesGoldenUnversionedAndReconnectionGreetings() {
        transport = new LoopbackTransport();
        start(NexusSession.builder(transport, new HandshakeDescriptor("0.20.0", null, 7, 300, new int[0]))
            .timers(timers).options(SessionOptions.defaults().withMaxMessageSize(65_526).withInvocationCredits(1)));
        assertArrayEquals(GoldenFrames.get("client-greeting-unversioned"), transport.server().readHandshake());

        transport = new LoopbackTransport();
        start(NexusSession.builder(transport, new HandshakeDescriptor("0.20.0", null, -5, 70_000, new int[0]))
            .timers(timers).reconnecting(true)
            .options(SessionOptions.defaults().withMaxMessageSize(16 * 1024 * 1024).withInvocationCredits(128)));
        assertArrayEquals(GoldenFrames.get("client-greeting-reconnection"), transport.server().readHandshake());
    }

    @Test
    void completesOnTheGoldenServerGreeting() throws Exception {
        NexusSession session = connected();
        assertTrue(session.isConnected());
        assertEquals(42, session.id());
        assertEquals(4 * 1024 * 1024, session.peerMaxMessageSize());
        assertEquals(32, session.peerInvocationCredits());
        assertEquals(4 * 1024 * 1024, session.sendMaxMessageSize());
        assertEquals(-1, session.advertisedIdleTimeoutMillis());
        assertEquals(10_000, session.pingIntervalMillis());
    }

    @Test
    void readsAnInt64SessionId() throws Exception {
        transport = new LoopbackTransport();
        NexusSession session = start(NexusSession.builder(transport,
            new HandshakeDescriptor("0.20.0", null, 7, 300, new int[0])).timers(timers));
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(GoldenFrames.get("server-greeting-large-id"));
        session.ready().get(5, TimeUnit.SECONDS);
        assertEquals(5_000_000_000L, session.id());
        assertEquals(65_526, session.sendMaxMessageSize());
    }

    private NexusSession connectedWithIdle(NexusSession.Builder builder, long idleTimeoutMillis) throws Exception {
        NexusSession session = start(builder);
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreetingWithIdle(CLIENT_HASH, 1, idleTimeoutMillis, 0));
        session.ready().get(5, TimeUnit.SECONDS);
        return session;
    }

    @Test
    void derivesThePingIntervalFromTheAdvertisedIdleTimeout() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreetingWithIdle(CLIENT_HASH, 1, 6_000, 2));
        session.ready().get(5, TimeUnit.SECONDS);
        assertEquals(6_000, session.advertisedIdleTimeoutMillis());
        assertEquals(2_000, session.pingIntervalMillis());

        timers.advance(1_999);
        timers.advance(1);
        assertEquals(MessageTypes.PING, transport.server().readByte());
    }

    @Test
    void aProgressProbeReportingAStallDisconnectsWithStalled() throws Exception {
        NexusSession session = start(builder().progressTimeoutMillis(1_000));
        AtomicReference<long[]> asked = new AtomicReference<>();
        session.addProgressProbe((now, timeout) -> {
            asked.set(new long[] {now, timeout});
            return now - session.nanoTime() == 0 && timeout == TimeUnit.SECONDS.toNanos(1) ? "test stall" : null;
        });
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(GoldenFrames.get("server-greeting"));
        session.ready().get(5, TimeUnit.SECONDS);
        assertEquals(1_000, session.progressTimeoutMillis());

        timers.advance(10_000);
        assertEquals(DisconnectReason.STALLED, closedWith(session));
        assertEquals(TimeUnit.SECONDS.toNanos(1), asked.get()[1]);
        assertEquals(37, transport.server().readByte());
    }

    @Test
    void probesReportingNothingLetTheSessionPing() throws Exception {
        NexusSession session = connected();
        session.addProgressProbe((now, timeout) -> null);
        session.addProgressProbe((now, timeout) -> {
            throw new IllegalStateException("a failing probe is logged and skipped");
        });
        timers.advance(10_000);
        assertEquals(MessageTypes.PING, transport.server().readByte());
        assertTrue(session.isConnected());
    }

    @Test
    void anExplicitIntervalIsUsedAsIs() throws Exception {
        List<String> warnings = new CopyOnWriteArrayList<>();
        NexusSession session = connectedWithIdle(builder().pingIntervalMillis(1_000).logger(warningsInto(warnings)),
            60_000);
        assertEquals(1_000, session.pingIntervalMillis());
        assertTrue(warnings.isEmpty(), "no warning below the idle timeout");
    }

    @Test
    void anExplicitIntervalIsNotCappedButWarnsWhenNotBelowTheIdleTimeout() throws Exception {
        List<String> warnings = new CopyOnWriteArrayList<>();
        NexusSession session = connectedWithIdle(builder().pingIntervalMillis(6_000).logger(warningsInto(warnings)),
            6_000);
        assertEquals(6_000, session.pingIntervalMillis());
        assertEquals(1, warnings.size());
        assertTrue(warnings.get(0).contains("6000 ms"), warnings.get(0));

        timers.advance(5_999);
        timers.advance(1);
        assertEquals(MessageTypes.PING, transport.server().readByte());
    }

    @Test
    void anExplicitIntervalDoesNotWarnWhenTheServerAdvertisesNone() throws Exception {
        List<String> warnings = new CopyOnWriteArrayList<>();
        NexusSession session = connectedWithIdle(builder().pingIntervalMillis(60_000).logger(warningsInto(warnings)),
            0);
        assertEquals(-1, session.advertisedIdleTimeoutMillis());
        assertEquals(60_000, session.pingIntervalMillis());
        assertTrue(warnings.isEmpty());
    }

    @Test
    void theDerivedIntervalIsAtLeastOneSecond() throws Exception {
        NexusSession session = connectedWithIdle(builder(), 1_500);
        assertEquals(1_500, session.advertisedIdleTimeoutMillis());
        assertEquals(1_000, session.pingIntervalMillis());
    }

    @Test
    void anAdvertisedZeroFallsBackToTenSeconds() throws Exception {
        NexusSession session = connectedWithIdle(builder(), 0);
        assertEquals(-1, session.advertisedIdleTimeoutMillis());
        assertEquals(10_000, session.pingIntervalMillis());
    }

    @Test
    void readsTheIdleTimeoutAsUInt32() throws Exception {
        NexusSession session = connectedWithIdle(builder(), 0xFFFF_FFFFL);
        assertEquals(0xFFFF_FFFFL, session.advertisedIdleTimeoutMillis());
        assertEquals(0xFFFF_FFFFL / 3, session.pingIntervalMillis());
    }

    @Test
    void aNegativeIdleTimeoutIsAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreetingWithIdle(CLIENT_HASH, 1, -1, 0));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void anIdleTimeoutAboveUInt32IsAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreetingWithIdle(CLIENT_HASH, 1, 0x1_0000_0000L, 0));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void effectivePingIntervalMatchesCSharp() {
        assertEquals(10_000, NexusSession.effectivePingInterval(null, 0));
        assertEquals(10_000, NexusSession.effectivePingInterval(null, -1));
        assertEquals(10_000, NexusSession.effectivePingInterval(null, 30_000));
        assertEquals(2_000, NexusSession.effectivePingInterval(null, 6_000));
        assertEquals(1_000, NexusSession.effectivePingInterval(null, 2_999));
        assertEquals(1_000, NexusSession.effectivePingInterval(null, 1));
        assertEquals(1_431_655_765, NexusSession.effectivePingInterval(null, 0xFFFF_FFFFL));
        assertEquals(50_000, NexusSession.effectivePingInterval(50_000, 6_000));
        assertEquals(1, NexusSession.effectivePingInterval(1, 0));
    }

    @Test
    void zeroOrNegativePingIntervalIsRejected() {
        assertThrows(IllegalArgumentException.class, () -> builder().pingIntervalMillis(0));
        assertThrows(IllegalArgumentException.class, () -> builder().pingIntervalMillis(-1));
        builder().pingIntervalMillis(null);
    }

    private static com.dtronix.nexnet.logging.NexusLogger warningsInto(List<String> warnings) {
        return (level, category, exception, message) -> {
            if (level == com.dtronix.nexnet.logging.NexusLogLevel.WARNING) {
                warnings.add(message);
            }
        };
    }

    @Test
    void clientMismatchSendsTheDisconnectByte() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreeting(CLIENT_HASH + 1, 1, 65_526, 1));
        assertEquals(DisconnectReason.CLIENT_MISMATCH, closedWith(session));
        assertEquals(24, transport.server().readByte());
        transport.server().expectEnd();
        ExecutionException e = assertThrows(ExecutionException.class, () -> session.ready().get());
        assertInstanceOf(SessionClosedException.class, e.getCause());
    }

    @Test
    void limitsBelowOneFrameAreAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreeting(CLIENT_HASH, 1, 65_525, 1));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
        assertEquals(22, transport.server().readByte());
    }

    @Test
    void zeroCreditsAreAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(FakeServer.serverGreeting(CLIENT_HASH, 1, 65_526, 0));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void badPreambleIsAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(new byte[] {0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 2});
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
        assertEquals(22, transport.server().readByte());
        transport.server().expectEnd();
    }

    @Test
    void messagesBeforeTheGreetingAreAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(GoldenFrames.get("invocation-credit"));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
        assertEquals(22, transport.server().readByte());
    }

    @Test
    void pingBeforeTheGreetingIsAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(new byte[] {MessageTypes.PING});
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void oversizedPreAuthBodyIsRefusedFromItsLength() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        // Only the header: the session must not wait for the 16385 body bytes.
        transport.server().write(new byte[] {(byte) MessageTypes.SERVER_GREETING, 0x01, 0x40});
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void aClientGreetingFromTheServerIsAProtocolError() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(GoldenFrames.get("client-greeting-unversioned"));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));
    }

    @Test
    void aDisconnectByteEndsTheSessionWithoutAReply() throws Exception {
        NexusSession session = start(builder());
        transport.server().readHandshake();
        transport.server().write(FakeServer.PREAMBLE);
        transport.server().write(new byte[] {29});
        assertEquals(DisconnectReason.AUTHENTICATION, closedWith(session));
        transport.server().expectEnd();
    }

    @Test
    void unassignedDisconnectCodesAreStillDisconnects() throws Exception {
        NexusSession session = connected();
        transport.server().write(new byte[] {26, MessageTypes.PING});
        assertEquals(DisconnectReason.UNKNOWN, closedWith(session));
        transport.server().expectEnd();
    }

    @Test
    void handshakeTimeoutClosesWithoutADisconnectByte() throws Exception {
        NexusSession session = start(builder().handshakeTimeoutMillis(15_000));
        transport.server().readHandshake();
        timers.advance(14_999);
        assertFalse(session.isClosing());
        timers.advance(1);
        assertEquals(DisconnectReason.TIMEOUT, closedWith(session));
        transport.server().expectEnd();
    }

    @Test
    void pingsOnTheIntervalAndTimesOutWhenNothingArrives() throws Exception {
        NexusSession session = connected();
        timers.advance(10_000);
        assertEquals(MessageTypes.PING, transport.server().readByte());
        timers.advance(10_000);
        assertEquals(MessageTypes.PING, transport.server().readByte());
        timers.advance(10_000);
        // 30 s without a frame: the tick checks the timeout before pinging.
        assertEquals(DisconnectReason.TIMEOUT, closedWith(session));
        assertEquals(23, transport.server().readByte());
        transport.server().expectEnd();
        assertEquals(0, timers.pending());
    }

    @Test
    void anyCompleteFrameRefreshesTheIdleTimeout() throws Exception {
        NexusSession session = connected();
        for (int i = 0; i < 5; i++) {
            timers.advance(10_000);
            assertEquals(MessageTypes.PING, transport.server().readByte());
            transport.server().write(new byte[] {MessageTypes.PING});
            awaitReceived(session);
        }
        assertTrue(session.isConnected());
    }

    private void awaitReceived(NexusSession session) throws InterruptedException {
        // The ping reply is read on the reader thread; give it a moment before the next tick.
        long deadline = System.currentTimeMillis() + 5_000;
        long before = timers.nanoTime();
        while (System.currentTimeMillis() < deadline) {
            if (session.lastReceivedNanosForTest() == before) {
                return;
            }
            Thread.sleep(2);
        }
        throw new AssertionError("The ping was not read.");
    }

    @Test
    void framesSplitAcrossReadsAndCoalescedInOneRead() throws Exception {
        List<InboundFrame> received = new CopyOnWriteArrayList<>();
        NexusSession session = builder().build();
        sessions.add(session);
        session.setMessageHandler(received::add);
        session.start();
        transport.server().readHandshake();
        byte[] greeting = concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting"));
        for (byte b : greeting) {
            transport.server().write(new byte[] {b});
        }
        session.ready().get(5, TimeUnit.SECONDS);
        byte[] pipeWrite = Frame.of(MessageTypes.DUPLEX_PIPE_WRITE, 0x0302, new byte[] {9, 8, 7}, 0, 3).bytes();
        byte[] chunk = Frame.of(MessageTypes.MESSAGE_CHUNK, 7 | (1 << 16), new byte[] {5}, 0, 1).bytes();
        transport.server().write(concat(concat(pipeWrite, chunk), GoldenFrames.get("invocation-cancellation")));
        long deadline = System.currentTimeMillis() + 5_000;
        while (received.size() < 3 && System.currentTimeMillis() < deadline) {
            Thread.sleep(2);
        }
        assertEquals(3, received.size());
        assertEquals(MessageTypes.DUPLEX_PIPE_WRITE, received.get(0).type());
        assertEquals(0x0302, received.get(0).pipeId());
        assertArrayEquals(new byte[] {9, 8, 7}, received.get(0).body());
        assertEquals(MessageTypes.MESSAGE_CHUNK, received.get(1).type());
        assertEquals(7, received.get(1).chunkInvocationId());
        assertEquals(1, received.get(1).chunkKind());
        assertEquals(MessageTypes.INVOCATION_CANCELLATION, received.get(2).type());
        assertArrayEquals(GoldenFrames.hex("91cd0201"), received.get(2).body());
    }

    @Test
    void aHandlerProtocolExceptionDisconnectsWithItsReason() throws Exception {
        NexusSession session = builder().build();
        sessions.add(session);
        session.setMessageHandler(frame -> {
            throw new SessionProtocolException(DisconnectReason.FLOW_CONTROL, "test");
        });
        session.start();
        transport.server().readHandshake();
        transport.server().write(concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting")));
        session.ready().get(5, TimeUnit.SECONDS);
        transport.server().write(GoldenFrames.get("invocation-credit"));
        assertEquals(DisconnectReason.FLOW_CONTROL, closedWith(session));
        assertEquals(35, transport.server().readByte());
    }

    @Test
    void unhandledAndUnknownMessagesAreProtocolErrors() throws Exception {
        NexusSession session = connected();
        transport.server().write(GoldenFrames.get("invocation-credit"));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(session));

        transport = new LoopbackTransport();
        NexusSession second = connected();
        transport.server().write(new byte[] {77, 0, 0});
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith(second));
        assertEquals(22, transport.server().readByte());
    }

    @Test
    void endOfStreamIsASocketError() throws Exception {
        NexusSession session = connected();
        transport.server().endStream();
        assertEquals(DisconnectReason.SOCKET_ERROR, closedWith(session));
    }

    @Test
    void gracefulDisconnectSendsTheByteThenClosesAfterTheDelay() throws Exception {
        NexusSession session = start(builder().options(SessionOptions.defaults().withDisconnectDelayMillis(200)));
        transport.server().readHandshake();
        transport.server().write(concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting")));
        session.ready().get(5, TimeUnit.SECONDS);
        CompletableFuture<DisconnectReason> closing = session.disconnect(DisconnectReason.GRACEFUL);
        assertEquals(21, transport.server().readByte());
        // The disconnect delay and the close fallback (progress timeout), which closing the transport cancels.
        waitFor(() -> timers.pending() == 2);
        assertFalse(closing.isDone());
        timers.advance(200);
        assertEquals(DisconnectReason.GRACEFUL, closing.get(5, TimeUnit.SECONDS));
        transport.server().expectEnd();
        ExecutionException e = assertThrows(ExecutionException.class,
            () -> session.send(Frame.headerOnly(MessageTypes.PING)).get());
        assertInstanceOf(SessionClosedException.class, e.getCause());
    }

    @Test
    void sendsFramesAndCoalescedControlMessages() throws Exception {
        NexusSession session = connected();
        session.send(Frame.of(MessageTypes.INVOCATION_CANCELLATION, GoldenFrames.hex("91cd0201")))
            .get(5, TimeUnit.SECONDS);
        assertArrayEquals(GoldenFrames.get("invocation-cancellation"), transport.server().readFrame());

        session.controlSender().requestInvocationCancellation(513);
        assertArrayEquals(GoldenFrames.get("invocation-cancellation"), transport.server().readFrame());
        session.controlSender().requestAbort(7, 1, 2);
        assertArrayEquals(GoldenFrames.get("message-abort"), transport.server().readFrame());

        // Advertised credits 32: credit is returned in batches of 16.
        for (int i = 0; i < 15; i++) {
            session.controlSender().returnInvocationCredit(1);
        }
        session.controlSender().requestPing();
        assertEquals(MessageTypes.PING, transport.server().readByte());
        session.controlSender().returnInvocationCredit(1);
        assertArrayEquals(GoldenFrames.get("invocation-credit"), transport.server().readFrame());
    }

    @Test
    void sendBeforeTheConnectionCompletesFails() {
        NexusSession session = start(builder());
        ExecutionException e = assertThrows(ExecutionException.class,
            () -> session.send(Frame.headerOnly(MessageTypes.PING)).get());
        assertInstanceOf(IllegalStateException.class, e.getCause());
    }

    @Test
    void threadsAreNamedDaemonsAndEndOnClose() throws Exception {
        int sessionsBefore = SessionCounters.openSessions();
        int threadsBefore = SessionCounters.liveThreads();
        NexusSession session = connected();
        assertEquals(sessionsBefore + 1, SessionCounters.openSessions());
        assertEquals(threadsBefore + 2, SessionCounters.liveThreads());
        List<Thread> threads = new ArrayList<>();
        for (Thread thread : Thread.getAllStackTraces().keySet()) {
            if (thread.getName().equals("nexnet-t-reader") || thread.getName().equals("nexnet-t-writer")) {
                threads.add(thread);
            }
        }
        assertEquals(2, threads.size());
        for (Thread thread : threads) {
            assertTrue(thread.isDaemon());
        }
        session.disconnect(DisconnectReason.GRACEFUL).get(5, TimeUnit.SECONDS);
        assertTrue(session.awaitThreads());
        assertEquals(sessionsBefore, SessionCounters.openSessions());
        assertEquals(threadsBefore, SessionCounters.liveThreads());
        assertEquals(0, timers.pending());
    }

    @Test
    void closedCompletesOnTheCallbackExecutorNotTheReader() throws Exception {
        ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> new Thread(r, "test-callbacks"));
        try {
            NexusSession session = start(builder().callbackExecutor(callbacks));
            transport.server().readHandshake();
            AtomicReference<String> thread = new AtomicReference<>();
            CompletableFuture<Void> seen = session.closed().thenAccept(r -> thread.set(Thread.currentThread().getName()));
            transport.server().write(new byte[] {0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1, 28});
            seen.get(5, TimeUnit.SECONDS);
            assertEquals("test-callbacks", thread.get());
            assertEquals(DisconnectReason.SERVER_SHUTDOWN, session.closed().get());
        } finally {
            callbacks.shutdownNow();
        }
    }

    @Test
    void aBlockedCallbackExecutorNeverStallsTheReader() throws Exception {
        // Deadlock guard: user code blocking in a callback must not stop frames from being read and dispatched.
        ExecutorService callbacks = Executors.newSingleThreadExecutor();
        CountDownLatch release = new CountDownLatch(1);
        try {
            callbacks.execute(() -> {
                try {
                    release.await();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                }
            });
            List<InboundFrame> received = new CopyOnWriteArrayList<>();
            NexusSession session = builder().callbackExecutor(callbacks).build();
            sessions.add(session);
            session.setMessageHandler(received::add);
            session.start();
            transport.server().readHandshake();
            transport.server().write(concat(FakeServer.PREAMBLE, GoldenFrames.get("server-greeting")));
            session.ready().get(5, TimeUnit.SECONDS);
            for (int i = 0; i < 100; i++) {
                transport.server().write(GoldenFrames.get("invocation-credit"));
            }
            waitFor(() -> received.size() == 100);
            transport.server().write(new byte[] {28});
            assertEquals(DisconnectReason.SERVER_SHUTDOWN, closedWith(session));
            assertFalse(session.closed().isDone());
            release.countDown();
            assertEquals(DisconnectReason.SERVER_SHUTDOWN, session.closed().get(5, TimeUnit.SECONDS));
        } finally {
            release.countDown();
            callbacks.shutdownNow();
        }
    }

    @Test
    void tokensLongerThan8KiBAreRefused() {
        assertThrows(IllegalArgumentException.class,
            () -> NexusSession.builder(transport, HANDSHAKE).authenticationToken(new byte[8193]));
        NexusSession.builder(transport, HANDSHAKE).authenticationToken(new byte[8192]);
    }

    @Test
    void startTwiceFails() {
        NexusSession session = start(builder());
        assertThrows(IllegalStateException.class, session::start);
        assertNotEquals(DisconnectReason.NONE, DisconnectReason.GRACEFUL);
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
