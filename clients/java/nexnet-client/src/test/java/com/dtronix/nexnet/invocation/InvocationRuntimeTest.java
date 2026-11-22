package com.dtronix.nexnet.invocation;

import static com.dtronix.nexnet.invocation.Peer.array;
import static com.dtronix.nexnet.invocation.Peer.bin;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.CancellationTokenSource;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.NexusRemoteInvocationException;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.NexusUnauthorizedException;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Jitter;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionClosedException;
import com.dtronix.nexnet.session.SessionCounters;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.transport.Transport;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class InvocationRuntimeTest {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {0, 1, 2, 3});
    static final byte[] SMALL_ARGS = {(byte) 0x91, 0x05};

    /** A client nexus method of the test nexus. */
    interface Method {
        CompletableFuture<Void> run(byte[] arguments, InvocationContext context);
    }

    static final class TestNexus extends ClientNexusBase<Object> {
        final Map<Integer, Method> methods = new ConcurrentHashMap<>();
        final Set<Integer> separate = ConcurrentHashMap.newKeySet();
        final List<String> threads = new CopyOnWriteArrayList<>();
        volatile NexusRemoteError error;

        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            threads.add(Thread.currentThread().getName());
            Method method = methods.get(methodId);
            if (method == null) {
                throw new IllegalArgumentException("Unknown method id " + methodId);
            }
            return method.run(TestCodec.currentArguments(), context);
        }

        @Override
        protected boolean separatePool(int methodId) {
            return separate.contains(methodId);
        }

        @Override
        protected NexusRemoteError onInvocationException(Throwable exception, int methodId) {
            return error;
        }
    }

    /** The callback executor's thread: callbacks are asserted to run on it by identity, not by name. */
    private final AtomicReference<Thread> callbackThread = new AtomicReference<>();
    private final ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> {
        Thread thread = daemon(r, "test-callbacks");
        callbackThread.set(thread);
        return thread;
    });
    private final Jitter.Tracked invocations =
        Jitter.executor(Executors.newCachedThreadPool(r -> daemon(r, "test-invocations")));
    private final TestNexus nexus = new TestNexus();
    private final List<NexusSession> sessions = new ArrayList<>();
    private final List<CountDownLatch> latches = new ArrayList<>();
    private NexusSession session;
    private InvocationRuntime runtime;
    private Peer peer;
    private final ManualTimers timers = new ManualTimers();
    private int progressTimeoutMillis = 30_000;
    /** The runtime's invocation executor; {@link #invocations} unless a test sets another before connecting. */
    private Executor invocationExecutor = invocations;

    private static Thread daemon(Runnable runnable, String name) {
        Thread thread = new Thread(runnable, name);
        thread.setDaemon(true);
        return thread;
    }

    private void connect(Transport transport, FakeServer server, int serverCredits, SessionOptions sessionOptions,
                         InvocationOptions options, boolean dispatch) throws Exception {
        session = NexusSession.builder(transport, HANDSHAKE).name("t").timers(timers)
            .progressTimeoutMillis(progressTimeoutMillis)
            .options(sessionOptions.withDisconnectDelayMillis(0)).build();
        sessions.add(session);
        runtime = InvocationRuntime.builder(session, HANDSHAKE, nexus).options(options).codec(TestCodec.INSTANCE)
            .callbackExecutor(callbacks).invocationExecutor(invocationExecutor).build();
        session.setMessageHandler(runtime);
        CompletableFuture<Void> ready = session.start();
        peer = new Peer(server);
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, serverCredits));
        ready.get(5, TimeUnit.SECONDS);
        runtime.connected();
        if (dispatch) {
            runtime.startDispatching();
        }
    }

    /** Connects with 32 server credits; the client advertises 2 credits, so every returned credit is sent at once. */
    private void connect(int serverCredits, InvocationOptions options, boolean dispatch) throws Exception {
        LoopbackTransport transport = new LoopbackTransport();
        connect(transport, transport.server(), serverCredits, SessionOptions.defaults().withInvocationCredits(2),
            options, dispatch);
    }

    private void connect() throws Exception {
        connect(32, InvocationOptions.defaults(), true);
    }

    private CountDownLatch latch() {
        CountDownLatch latch = new CountDownLatch(1);
        latches.add(latch);
        return latch;
    }

    @AfterEach
    void tearDown() throws Exception {
        for (CountDownLatch latch : latches) {
            latch.countDown();
        }
        for (NexusSession s : sessions) {
            s.abort(DisconnectReason.GRACEFUL);
            assertTrue(s.awaitThreads(), "threads ended");
        }
        // Queued completions run first (a closed session fails its pending calls on the callback executor), then the
        // pools end. shutdownNow() would drop them, and the dropped calls would stay pending for the leak audit.
        callbacks.shutdown();
        invocations.shutdown();
        assertTrue(callbacks.awaitTermination(5, TimeUnit.SECONDS), "callbacks ended");
        assertTrue(invocations.awaitTermination(5, TimeUnit.SECONDS), "invocations ended");
    }

    private static <T> T await(CompletableFuture<T> future) throws Exception {
        return future.get(5, TimeUnit.SECONDS);
    }

    private static Throwable failure(CompletableFuture<?> future) throws Exception {
        try {
            future.get(5, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            return e.getCause();
        } catch (CancellationException e) {
            return e;
        }
        throw new AssertionError("The future completed normally.");
    }

    private static void waitFor(java.util.function.BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not met.");
            }
            Thread.sleep(2);
        }
    }

    private DisconnectReason closedWith() throws Exception {
        return session.closedInternal().get(5, TimeUnit.SECONDS);
    }

    private static int idOf(Peer.Msg msg) {
        WireIn r = new WireIn(msg.body, 0, msg.body.length);
        r.arrayHeader();
        return r.readUnsigned(0xFFFF);
    }

    private static byte[] invocation(int id, int methodId, int flags, byte[] arguments) {
        return array(id, methodId, flags, arguments);
    }

    private static CompletableFuture<Void> value(InvocationContext context, byte[] value) {
        return context.result(CompletableFuture.completedFuture(value), TestCodec.BYTES);
    }

    // ------------------------------------------------------------------ outbound

    @Test
    void invokeSendsTheInvocationAndCompletesWithTheResultOnTheCallbackExecutor() throws Exception {
        connect();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        CompletableFuture<Thread> thread = call.thenApply(v -> Thread.currentThread());
        Peer.Msg msg = peer.expect(MessageTypes.INVOCATION);
        assertArrayEquals(invocation(1, 1, 0, SMALL_ARGS), msg.body);
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1, new byte[] {0x2A}));
        // Wait for the dependent first: CompletableFuture.get() on 'call' helps run call's pending dependents on the
        // waiting thread, so awaiting 'call' first could run the thenApply above on this test thread.
        Thread completedOn = await(thread);
        assertSame(callbackThread.get(), completedOn);
        assertArrayEquals(new byte[] {0x2A}, await(call));
        assertEquals(0, runtime.pendingCalls());
    }

    @Test
    void aMethodWithoutArgumentsSendsAnEmptyArray() throws Exception {
        connect();
        CompletableFuture<Void> call = runtime.invokeTask(2, ArgumentWriter.NONE, CancellationToken.NONE);
        assertArrayEquals(invocation(1, 2, 0, new byte[] {(byte) 0x90}), peer.expect(MessageTypes.INVOCATION).body);
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1));
        assertNull(await(call));
    }

    @Test
    void resultStatesMapToExceptions() throws Exception {
        connect();
        List<CompletableFuture<byte[]>> calls = new ArrayList<>();
        for (int i = 0; i < 5; i++) {
            calls.add(runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null));
            assertEquals(i + 1, idOf(peer.expect(MessageTypes.INVOCATION)));
        }
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 2));
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 2, array(7, "System.InvalidOperationException", "boom")));
        peer.send(MessageTypes.INVOCATION_RESULT, array(3, 2, array("not", "a detail")));
        peer.send(MessageTypes.INVOCATION_RESULT, array(4, 3));
        peer.send(MessageTypes.INVOCATION_RESULT, array(5, 4));

        NexusRemoteInvocationException plain = assertInstanceOf(NexusRemoteInvocationException.class,
            failure(calls.get(0)));
        assertFalse(plain instanceof NexusUnauthorizedException);
        assertNull(plain.remoteErrorCode());
        assertNull(plain.remoteExceptionType());
        assertNull(plain.remoteMessage());

        NexusRemoteInvocationException detailed = assertInstanceOf(NexusRemoteInvocationException.class,
            failure(calls.get(1)));
        assertEquals(7, detailed.remoteErrorCode());
        assertEquals("System.InvalidOperationException", detailed.remoteExceptionType());
        assertEquals("boom", detailed.remoteMessage());

        NexusRemoteInvocationException malformed = assertInstanceOf(NexusRemoteInvocationException.class,
            failure(calls.get(2)));
        assertNull(malformed.remoteMessage());

        assertInstanceOf(NexusUnauthorizedException.class, failure(calls.get(3)));
        assertInstanceOf(CancellationException.class, failure(calls.get(4)));
        assertTrue(calls.get(4).isCancelled());
        assertTrue(session.isConnected());
    }

    @Test
    void aTokenCanceledBeforeSendFailsTheCallAndSendsNothing() throws Exception {
        connect();
        CancellationTokenSource source = new CancellationTokenSource();
        source.cancel();
        CompletableFuture<byte[]> canceled = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES,
            source.token());
        assertInstanceOf(CancellationException.class, failure(canceled));
        assertTrue(canceled.isCancelled());

        // Nothing was sent and no id was taken: the next call is the first invocation, with id 1.
        CompletableFuture<Void> next = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        assertArrayEquals(invocation(1, 2, 0, new byte[] {(byte) 0x90}), peer.expect(MessageTypes.INVOCATION).body);
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1));
        await(next);
    }

    @Test
    void cancelAfterSendSendsACancellationAndConsumesTheLateResult() throws Exception {
        connect();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        assertEquals(1, idOf(peer.expect(MessageTypes.INVOCATION)));
        assertTrue(call.cancel(true));
        assertTrue(call.isCancelled());
        assertArrayEquals(array(1), peer.expect(MessageTypes.INVOCATION_CANCELLATION).body);
        // Complete, so not awaited (as in C# and JS), but its id stays reserved for the late result.
        assertEquals(0, runtime.snapshot(session.nanoTime()).awaitingResults());
        assertEquals(1, runtime.reservedIdCount());

        // The id stays reserved until the late result, which is consumed without a protocol error.
        CompletableFuture<byte[]> second = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        assertEquals(2, idOf(peer.expect(MessageTypes.INVOCATION)));
        assertEquals(1, runtime.snapshot(session.nanoTime()).awaitingResults());
        assertEquals(2, runtime.reservedIdCount());
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1, new byte[] {0x01}));
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 1, new byte[] {0x02}));
        assertArrayEquals(new byte[] {0x02}, await(second));
        assertTrue(session.isConnected());
        assertEquals(0, runtime.pendingCalls());
        assertEquals(0, runtime.reservedIdCount());
        assertEquals(0, runtime.snapshot(session.nanoTime()).awaitingResults());
    }

    @Test
    void aTokenCanceledAfterSendCancelsRemotely() throws Exception {
        connect();
        CancellationTokenSource source = new CancellationTokenSource();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES,
            source.token());
        assertEquals(1, idOf(peer.expect(MessageTypes.INVOCATION)));
        source.cancel();
        assertInstanceOf(CancellationException.class, failure(call));
        assertArrayEquals(array(1), peer.expect(MessageTypes.INVOCATION_CANCELLATION).body);
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 4));
        CompletableFuture<Void> next = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        assertEquals(2, idOf(peer.expect(MessageTypes.INVOCATION)));
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 1));
        await(next);
    }

    @Test
    void callsWaitForInvocationCreditInOrder() throws Exception {
        connect(1, InvocationOptions.defaults(), true);
        CompletableFuture<byte[]> first = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        assertEquals(1, idOf(peer.expect(MessageTypes.INVOCATION)));
        CompletableFuture<Void> second = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        runtime.invokeVoid(3, TestCodec.args(new byte[] {(byte) 0x91, 0x09}));
        CancellationTokenSource source = new CancellationTokenSource();
        CompletableFuture<Void> canceled = runtime.invokeTask(2, ArgumentWriter.NONE, source.token());
        assertEquals(4, runtime.pendingCalls());

        // Canceled while it waits for credit: completes canceled and is never sent.
        source.cancel();
        assertInstanceOf(CancellationException.class, failure(canceled));

        peer.send(MessageTypes.INVOCATION_CREDIT, array(1));
        assertArrayEquals(invocation(2, 2, 0, new byte[] {(byte) 0x90}), peer.expect(MessageTypes.INVOCATION).body);
        peer.send(MessageTypes.INVOCATION_CREDIT, array(1));
        // Fire-and-forget waits for credit too, and is sent with IgnoreReturn and id 0.
        assertArrayEquals(invocation(0, 3, 1, new byte[] {(byte) 0x91, 0x09}),
            peer.expect(MessageTypes.INVOCATION).body);
        peer.send(MessageTypes.INVOCATION_CREDIT, array(1));
        CompletableFuture<Void> last = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        assertEquals(3, idOf(peer.expect(MessageTypes.INVOCATION)));

        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1, new byte[] {0x01}));
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 1));
        peer.send(MessageTypes.INVOCATION_RESULT, array(3, 1));
        await(first);
        await(second);
        await(last);
    }

    @Test
    void zeroInvocationCreditIsAFlowControlError() throws Exception {
        connect();
        peer.send(MessageTypes.INVOCATION_CREDIT, array(0));
        assertEquals(DisconnectReason.FLOW_CONTROL, closedWith());
    }

    @Test
    void creditReturnedBeyondWhatWasTakenIsAFlowControlError() throws Exception {
        connect();
        peer.send(MessageTypes.INVOCATION_CREDIT, array(1));
        assertEquals(DisconnectReason.FLOW_CONTROL, closedWith());
    }

    @Test
    void aResultForAnInvocationNotWaitingIsAProtocolError() throws Exception {
        connect();
        peer.send(MessageTypes.INVOCATION_RESULT, array(77, 1));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    @Test
    void theSessionEndingCancelsSentAndWaitingCalls() throws Exception {
        connect(1, InvocationOptions.defaults(), true);
        CompletableFuture<byte[]> sent = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        peer.expect(MessageTypes.INVOCATION);
        CompletableFuture<Void> waiting = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        peer.server().endStream();
        DisconnectReason reason = closedWith();
        // C-2: canceled, as in C#, with the disconnect reason as the cause.
        for (CompletableFuture<?> call : Arrays.asList(sent, waiting)) {
            Throwable error = failure(call);
            assertInstanceOf(CancellationException.class, error);
            SessionClosedException cause = assertInstanceOf(SessionClosedException.class, error.getCause());
            assertEquals(reason, cause.reason());
        }
        assertEquals(0, runtime.pendingCalls());
        // After the session ended, calls fail at once.
        assertInstanceOf(CancellationException.class, failure(runtime.invokeTask(2, ArgumentWriter.NONE, null)));
    }

    @Test
    void invocationsQueuedBeforeADisconnectAreSentBeforeTheByteAndResultCallsEndCanceled() throws Exception {
        // K25, specification section 5.6: the invocation being written and the ones queued behind it go out before
        // the disconnect byte. Calls waiting for a result end canceled (C-2), as in C#: the session ends before any
        // result can arrive. The IgnoreReturn invocation completes once written.
        GatedTransport gated = new GatedTransport();
        connect(gated, gated.inner().server(), 32, SessionOptions.defaults().withInvocationCredits(2),
            InvocationOptions.defaults(), true);
        gated.limit(0);
        CompletableFuture<byte[]> inProgress = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        waitFor(gated::isBlocked);
        List<CompletableFuture<?>> queued = new ArrayList<>();
        queued.add(runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null));
        queued.add(runtime.invokeTask(2, ArgumentWriter.NONE, null));
        CompletableFuture<Void> ignoreReturn = runtime.invokeIgnoreReturn(3, 1, SMALL_ARGS);
        waitFor(() -> SessionCounters.pendingWrites() == 3);
        CompletableFuture<DisconnectReason> closing = session.disconnect(DisconnectReason.GRACEFUL);
        gated.open();

        assertArrayEquals(invocation(1, 1, 0, SMALL_ARGS), peer.expect(MessageTypes.INVOCATION).body);
        assertArrayEquals(invocation(2, 1, 0, SMALL_ARGS), peer.expect(MessageTypes.INVOCATION).body);
        assertArrayEquals(invocation(3, 2, 0, new byte[] {(byte) 0x90}), peer.expect(MessageTypes.INVOCATION).body);
        assertArrayEquals(invocation(0, 3, 1, SMALL_ARGS), peer.expect(MessageTypes.INVOCATION).body);
        assertEquals(21, peer.next().type);
        assertEquals(DisconnectReason.GRACEFUL, closing.get(5, TimeUnit.SECONDS));
        peer.server().expectEnd();
        queued.add(0, inProgress);
        for (CompletableFuture<?> call : queued) {
            Throwable error = failure(call);
            assertInstanceOf(CancellationException.class, error);
            assertEquals(DisconnectReason.GRACEFUL,
                assertInstanceOf(SessionClosedException.class, error.getCause()).reason());
        }
        await(ignoreReturn);
        assertEquals(0, runtime.pendingCalls());
    }

    // ------------------------------------------------------------------ inbound

    @Test
    void anInvocationRunsOnTheInvocationExecutorAndSendsItsResult() throws Exception {
        connect();
        List<byte[]> received = new CopyOnWriteArrayList<>();
        nexus.methods.put(0, (args, ctx) -> {
            received.add(args);
            return value(ctx, new byte[] {(byte) 0xA1, 0x78});
        });
        peer.send(MessageTypes.INVOCATION, invocation(9, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(9, 1, new byte[] {(byte) 0xA1, 0x78}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        assertArrayEquals(SMALL_ARGS, received.get(0));
        assertEquals("test-invocations", nexus.threads.get(0));
        // The credit was returned when the invocation was dequeued for execution. The writer sends pending control
        // frames after the queued frames of its batch, so the credit can follow the result on the wire: wait for it.
        peer.awaitCredits(1);
        assertEquals(1, peer.creditsReturned);
        // The result can reach the server before the runtime retires the invocation.
        awaitInboundInvocations(0);
    }

    @Test
    void aFireAndForgetInvocationSendsNoResult() throws Exception {
        connect();
        CountDownLatch ran = new CountDownLatch(1);
        nexus.methods.put(1, (args, ctx) -> {
            ran.countDown();
            return ctx.noResult();
        });
        nexus.methods.put(2, (args, ctx) -> ctx.noResult());
        peer.send(MessageTypes.INVOCATION, invocation(0, 1, 1, SMALL_ARGS));
        assertTrue(ran.await(5, TimeUnit.SECONDS));
        // A method that returns no result for an invocation that expects one still completes it.
        peer.send(MessageTypes.INVOCATION, invocation(4, 2, 0, new byte[] {(byte) 0x90}));
        assertArrayEquals(array(4, 1), peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    @Test
    void anUnknownMethodIsAProtocolError() throws Exception {
        connect();
        peer.send(MessageTypes.INVOCATION, invocation(1, 9, 0, SMALL_ARGS));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    @Test
    void creditIsReturnedAtDequeueAndAnInvocationWithoutCreditIsAFlowControlError() throws Exception {
        connect(32, InvocationOptions.defaults().withMaxConcurrentInvocations(1), true);
        CountDownLatch release = latch();
        nexus.methods.put(0, (args, ctx) -> ctx.result(CompletableFuture.supplyAsync(() -> {
            try {
                release.await();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            return new byte[] {0x01};
        }), TestCodec.BYTES));
        // Two credits advertised: the first is dequeued at once (credit back), the next two queue behind it.
        peer.send(MessageTypes.INVOCATION, invocation(1, 0, 0, SMALL_ARGS));
        peer.awaitCredits(1);
        peer.send(MessageTypes.INVOCATION, invocation(2, 0, 0, SMALL_ARGS));
        peer.send(MessageTypes.INVOCATION, invocation(3, 0, 0, SMALL_ARGS));
        peer.send(MessageTypes.INVOCATION, invocation(4, 0, 0, SMALL_ARGS));
        assertEquals(DisconnectReason.FLOW_CONTROL, closedWith());
    }

    @Test
    void queuedInvocationsRunOneAtATimeAndReturnCreditAsTheyStart() throws Exception {
        connect(32, InvocationOptions.defaults().withMaxConcurrentInvocations(1), true);
        AtomicInteger concurrent = new AtomicInteger();
        AtomicInteger peak = new AtomicInteger();
        nexus.methods.put(0, (args, ctx) -> {
            peak.accumulateAndGet(concurrent.incrementAndGet(), Math::max);
            CompletableFuture<byte[]> result = CompletableFuture.supplyAsync(() -> {
                concurrent.decrementAndGet();
                return new byte[] {args[1]};
            });
            return ctx.result(result, TestCodec.BYTES);
        });
        peer.send(MessageTypes.INVOCATION, invocation(1, 0, 0, new byte[] {(byte) 0x91, 0x01}));
        peer.send(MessageTypes.INVOCATION, invocation(2, 0, 0, new byte[] {(byte) 0x91, 0x02}));
        assertArrayEquals(array(1, 1, new byte[] {0x01}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        assertArrayEquals(array(2, 1, new byte[] {0x02}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        peer.awaitCredits(2);
        assertEquals(1, peak.get());
    }

    @Test
    void aFailedMethodSendsAnExceptionWithTheDetailItsSettingsAllow() throws Exception {
        connect();
        nexus.methods.put(0, (args, ctx) -> {
            CompletableFuture<byte[]> failed = new CompletableFuture<>();
            failed.completeExceptionally(new IllegalStateException("secret"));
            return ctx.result(failed, TestCodec.BYTES);
        });
        nexus.methods.put(1, (args, ctx) -> {
            throw new IllegalStateException("thrown by dispatch");
        });
        peer.send(MessageTypes.INVOCATION, invocation(1, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(1, 2), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        peer.send(MessageTypes.INVOCATION, invocation(2, 1, 0, SMALL_ARGS));
        assertArrayEquals(array(2, 2), peer.expect(MessageTypes.INVOCATION_RESULT).body);

        // onInvocationException replaces the detail whatever the setting.
        nexus.error = new NexusRemoteError(42, "nope");
        peer.send(MessageTypes.INVOCATION, invocation(3, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(3, 2, array(42, null, "nope")), peer.expect(MessageTypes.INVOCATION_RESULT).body);

        // An error with only a message sends a nil code, as the C# NexusRemoteError(null, message) does.
        nexus.error = new NexusRemoteError(null, "message only");
        peer.send(MessageTypes.INVOCATION, invocation(4, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(4, 2, array(null, null, "message only")),
            peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    @Test
    void typeAndMessageDetailIsSentWhenConfigured() throws Exception {
        connect(32, InvocationOptions.defaults().withRemoteExceptionDetail(RemoteExceptionDetail.TYPE_AND_MESSAGE),
            true);
        nexus.methods.put(0, (args, ctx) -> {
            throw new IllegalStateException("boom");
        });
        peer.send(MessageTypes.INVOCATION, invocation(1, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(1, 2, array(null, "java.lang.IllegalStateException", "boom")),
            peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    @Test
    void aCancellationCancelsTheInvocationsToken() throws Exception {
        connect();
        CountDownLatch started = new CountDownLatch(1);
        nexus.methods.put(0, (args, ctx) -> {
            CompletableFuture<byte[]> result = new CompletableFuture<>();
            ctx.cancellationToken().register(() -> result.cancel(true));
            started.countDown();
            return ctx.result(result, TestCodec.BYTES);
        });
        peer.send(MessageTypes.INVOCATION, invocation(5, 0, 0, SMALL_ARGS));
        assertTrue(started.await(5, TimeUnit.SECONDS));
        peer.send(MessageTypes.INVOCATION_CANCELLATION, array(5));
        assertArrayEquals(array(5, 4), peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    /** Method 0 answers canceled when its token is already canceled as it starts, and 0x01 otherwise. */
    private void answerCanceledWhenStartedCanceled() {
        nexus.methods.put(0, (args, ctx) -> {
            CompletableFuture<byte[]> result = new CompletableFuture<>();
            if (ctx.cancellationToken().isCancellationRequested()) {
                result.cancel(true);
            } else {
                result.complete(new byte[] {0x01});
            }
            return ctx.result(result, TestCodec.BYTES);
        });
    }

    /** Queues invocation 5 and its cancellation while dispatching is off; returns once the reader handled both. */
    private void queueInvocationAndItsCancellation() throws Exception {
        // An outbound call whose result is sent last: frames are handled in order, so once its result is in, the
        // reader has handled the invocation and its cancellation too.
        CompletableFuture<Void> barrier = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        assertEquals(1, idOf(peer.expect(MessageTypes.INVOCATION)));
        peer.send(MessageTypes.INVOCATION, invocation(5, 0, 0, SMALL_ARGS));
        peer.send(MessageTypes.INVOCATION_CANCELLATION, array(5));
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1));
        await(barrier);
        // Nothing runs before dispatching starts (after onConnected).
        assertTrue(nexus.threads.isEmpty());
        assertEquals(1, runtime.inboundInvocations());
    }

    @Test
    void aQueuedInvocationCanceledBeforeItStartsRunsWithACanceledToken() throws Exception {
        connect(32, InvocationOptions.defaults(), false);
        answerCanceledWhenStartedCanceled();
        queueInvocationAndItsCancellation();
        // K46: the reader cancels the token of an invocation that has not started at once, so nothing needs to run
        // on the invocation executor first.
        runtime.startDispatching();
        assertArrayEquals(array(5, 4), peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    @Test
    void aQueuedInvocationCanceledBeforeItStartsRunsCanceledWhateverTheExecutorsOrder() throws Exception {
        // K46: a multi-threaded executor can start the invocation before a cancellation handed to it. Hold the tasks
        // and run them newest first, one at a time, so a cancellation sent through the executor would come too late.
        HeldExecutor held = new HeldExecutor(invocations);
        invocationExecutor = held;
        connect(32, InvocationOptions.defaults(), false);
        answerCanceledWhenStartedCanceled();
        queueInvocationAndItsCancellation();
        runtime.startDispatching();
        held.releaseNewestFirst();
        assertArrayEquals(array(5, 4), peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    /** Holds tasks until {@link #releaseNewestFirst()}, then passes them to the jittered executor. */
    private static final class HeldExecutor implements Executor {
        private final Jitter.Tracked target;
        private final List<Runnable> held = new ArrayList<>();
        private boolean released;

        HeldExecutor(Jitter.Tracked target) {
            this.target = target;
        }

        @Override
        public void execute(Runnable command) {
            synchronized (held) {
                if (!released) {
                    held.add(command);
                    return;
                }
            }
            target.execute(command);
        }

        /** Runs the held tasks newest first, each to its end (and what it started) before the next. */
        void releaseNewestFirst() throws InterruptedException {
            List<Runnable> tasks;
            synchronized (held) {
                released = true;
                tasks = new ArrayList<>(held);
                held.clear();
            }
            for (int i = tasks.size() - 1; i >= 0; i--) {
                target.execute(tasks.get(i));
                assertTrue(target.awaitIdle(5, TimeUnit.SECONDS), "held task ran");
            }
        }
    }

    @Test
    void separatePoolMethodsRunBesideABusyNormalPool() throws Exception {
        connect(32, InvocationOptions.defaults().withMaxConcurrentInvocations(1), true);
        CountDownLatch release = latch();
        nexus.methods.put(0, (args, ctx) -> ctx.result(CompletableFuture.supplyAsync(() -> {
            try {
                release.await();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            return new byte[] {0x00};
        }), TestCodec.BYTES));
        nexus.methods.put(1, (args, ctx) -> value(ctx, new byte[] {0x01}));
        nexus.separate.add(1);
        peer.send(MessageTypes.INVOCATION, invocation(1, 0, 0, SMALL_ARGS));
        peer.send(MessageTypes.INVOCATION, invocation(2, 0, 0, SMALL_ARGS));
        peer.send(MessageTypes.INVOCATION, invocation(3, 1, 0, SMALL_ARGS));
        assertArrayEquals(array(3, 1, new byte[] {0x01}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        release.countDown();
        assertArrayEquals(array(1, 1, new byte[] {0x00}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        assertArrayEquals(array(2, 1, new byte[] {0x00}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
    }

    // ------------------------------------------------------------------ large messages

    private static byte[] largeArguments(int size) {
        byte[] value = bin(size);
        byte[] args = new byte[1 + value.length];
        args[0] = (byte) 0x91;
        System.arraycopy(value, 0, args, 1, value.length);
        return args;
    }

    @Test
    void aLargeArgumentIsStreamed() throws Exception {
        connect();
        byte[] args = largeArguments(150_000);
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(args), TestCodec.BYTES, null);
        assertArrayEquals(invocation(1, 1, 0, args), peer.readStream(1, 0, MessageTypes.INVOCATION));
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1, new byte[] {0x01}));
        assertArrayEquals(new byte[] {0x01}, await(call));
        assertEquals(0, runtime.pendingCalls());
    }

    @Test
    void anArgumentLargerThanTheServerAcceptsFailsLocally() throws Exception {
        connect();
        byte[] args = largeArguments(4 * 1024 * 1024);
        assertInstanceOf(com.dtronix.nexnet.NexusMessageTooLargeException.class,
            failure(runtime.invoke(1, TestCodec.args(args), TestCodec.BYTES, null)));
        CompletableFuture<Void> next = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        assertEquals(1, idOf(peer.expect(MessageTypes.INVOCATION)));
        peer.send(MessageTypes.INVOCATION_RESULT, array(1, 1));
        await(next);
    }

    /** Connects over a gated transport that lets the MessageStart and one chunk through. */
    private GatedTransport gatedConnect() throws Exception {
        GatedTransport transport = new GatedTransport();
        connect(transport, transport.inner().server(), 32, SessionOptions.defaults().withInvocationCredits(2),
            InvocationOptions.defaults(), true);
        // MessageStart [1, 0, 110, uint32] is a 12-byte frame; a full chunk is 6 + 65535 bytes.
        transport.limit(12 + 6 + 65_535);
        return transport;
    }

    /** Reads chunks until the abort and returns it. */
    private byte[] readUntilAbort(int total) {
        int received = 0;
        while (true) {
            Peer.Msg msg = peer.next();
            if (msg.type == MessageTypes.MESSAGE_ABORT) {
                assertTrue(received < total, "the stream stopped early");
                return msg.body;
            }
            assertEquals(MessageTypes.MESSAGE_CHUNK, msg.type);
            received += msg.body.length;
        }
    }

    @Test
    void aRefusedLargeArgumentFailsTheCallAndIsAcked() throws Exception {
        GatedTransport transport = gatedConnect();
        byte[] args = largeArguments(300_000);
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(args), TestCodec.BYTES, null);
        assertEquals(1, idOf(peer.expect(MessageTypes.MESSAGE_START)));
        peer.send(MessageTypes.MESSAGE_ABORT, array(1, 0, 0));
        assertInstanceOf(NexusBusyException.class, failure(call));
        transport.open();
        assertArrayEquals(array(1, 0, 3), readUntilAbort(args.length));
        // The id was released and nothing else follows for it.
        CompletableFuture<Void> next = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        Peer.Msg msg = peer.expect(MessageTypes.INVOCATION);
        peer.send(MessageTypes.INVOCATION_RESULT, array(idOf(msg), 1));
        await(next);
        // The refused (or canceled) call's id is released by the stream's pump thread after it queues the abort,
        // which the peer can read first.
        waitFor(() -> runtime.pendingCalls() == 0);
    }

    @Test
    void cancelingWhileALargeArgumentStreamsSendsCancelled() throws Exception {
        GatedTransport transport = gatedConnect();
        byte[] args = largeArguments(300_000);
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(args), TestCodec.BYTES, null);
        Peer.Msg start = peer.expect(MessageTypes.MESSAGE_START);
        assertEquals(1, idOf(start));
        peer.expect(MessageTypes.MESSAGE_CHUNK);
        assertTrue(call.cancel(true));
        transport.open();
        assertArrayEquals(array(1, 0, 2), readUntilAbort(args.length - 65_535));
        // No InvocationCancellation follows: the method never runs.
        CompletableFuture<Void> next = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        Peer.Msg msg = peer.expect(MessageTypes.INVOCATION);
        peer.send(MessageTypes.INVOCATION_RESULT, array(idOf(msg), 1));
        await(next);
        // The refused (or canceled) call's id is released by the stream's pump thread after it queues the abort,
        // which the peer can read first.
        waitFor(() -> runtime.pendingCalls() == 0);
    }

    @Test
    void aLargeResultIsReassembled() throws Exception {
        connect();
        long idle = runtime.budget().reserved();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        peer.expect(MessageTypes.INVOCATION);
        byte[] value = bin(200_000);
        peer.stream(1, 1, MessageTypes.INVOCATION_RESULT, array(1, 1, value));
        assertArrayEquals(value, await(call));
        // The reservation is released once the result has been read.
        assertEquals(idle, runtime.budget().reserved());
    }

    @Test
    void aLargeInvocationIsDispatchedAndItsLargeResultStreamed() throws Exception {
        connect();
        nexus.methods.put(0, (args, ctx) -> value(ctx, Arrays.copyOfRange(args, 1, args.length)));
        byte[] args = largeArguments(120_000);
        peer.stream(4, 0, MessageTypes.INVOCATION, invocation(4, 0, 0, args));
        byte[] result = peer.readStream(4, 1, MessageTypes.INVOCATION_RESULT);
        assertArrayEquals(array(4, 1, Arrays.copyOfRange(args, 1, args.length)), result);
        // The credit can follow the result on the wire (control frames go after a batch's queued frames).
        peer.awaitCredits(1);
        assertEquals(1, peer.creditsReturned);
    }

    @Test
    void aLargeMessageThatDoesNotFitTheBudgetIsRefusedWithBusy() throws Exception {
        connect(32, InvocationOptions.defaults().withSessionBudget(64 * 1024), true);
        nexus.methods.put(0, (args, ctx) -> value(ctx, new byte[] {0x01}));
        peer.stream(4, 0, MessageTypes.INVOCATION, invocation(4, 0, 0, largeArguments(70_000)));
        assertArrayEquals(array(4, 0, 0), peer.expect(MessageTypes.MESSAGE_ABORT).body);
        // The refused argument's credit came back; the session goes on.
        peer.send(MessageTypes.INVOCATION, invocation(5, 0, 0, SMALL_ARGS));
        assertArrayEquals(array(5, 1, new byte[] {0x01}), peer.expect(MessageTypes.INVOCATION_RESULT).body);
        peer.awaitCredits(2);
        assertTrue(session.isConnected());
    }

    @Test
    void aStartWithAMismatchedInnerTypeIsAProtocolError() throws Exception {
        connect();
        peer.send(MessageTypes.MESSAGE_START, array(1, 0, MessageTypes.INVOCATION_RESULT, 70_000));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    @Test
    void aChunkWithoutAStreamIsAProtocolError() throws Exception {
        connect();
        peer.sendChunk(1, 0, new byte[10], 0, 10);
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    @Test
    void aResultStreamForAnInvocationNotWaitingIsAProtocolError() throws Exception {
        connect();
        peer.send(MessageTypes.MESSAGE_START, array(3, 1, MessageTypes.INVOCATION_RESULT, 70_000));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    @Test
    void aStartLargerThanTheAdvertisedLimitIsAProtocolError() throws Exception {
        connect();
        peer.send(MessageTypes.MESSAGE_START, array(3, 0, MessageTypes.INVOCATION, 4 * 1024 * 1024 + 1));
        assertEquals(DisconnectReason.PROTOCOL_ERROR, closedWith());
    }

    // ------------------------------------------------------------------ progress (K17)

    private void awaitInboundInvocations(int count) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (runtime.inboundInvocations() != count) {
            assertTrue(System.nanoTime() < deadline, "inbound invocations: " + count);
            Thread.sleep(5);
        }
    }

    private void awaitLargeMessages(int count) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (runtime.largeMessagesReceiving() != count) {
            assertTrue(System.nanoTime() < deadline, "large messages being received: " + count);
            Thread.sleep(5);
        }
    }

    @Test
    void anIncomingLargeResultWhoseDataStopsDisconnectsWithStalled() throws Exception {
        progressTimeoutMillis = 1_000;
        connect();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        peer.expect(MessageTypes.INVOCATION);
        CompletableFuture<Void> barrier = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        peer.expect(MessageTypes.INVOCATION);
        peer.send(MessageTypes.MESSAGE_START, array(1, 1, MessageTypes.INVOCATION_RESULT, 200_000));
        peer.sendChunk(1, 1, new byte[1000], 0, 1000);
        // Frames are handled in order: once the barrier's result is in, so is the chunk. Otherwise the reader could
        // handle the chunk after the clock moved below, and the stream would not look stalled.
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 1));
        await(barrier);
        awaitLargeMessages(1);
        // The peer can read the barrier invocation before the writer thread clears its write-in-progress flag. A tick
        // that still saw it would find a stalled write and close without the disconnect byte (the K43 flake: -1).
        ManualTimers.awaitWriterIdle(session);

        // The first keepalive tick (10 s): nothing arrived for the stream for longer than the progress timeout.
        timers.advance(10_000);
        assertEquals(DisconnectReason.STALLED, closedWith());
        assertEquals(37, peer.server().readByte());
        Throwable error = failure(call);
        assertInstanceOf(CancellationException.class, error);
        assertEquals(DisconnectReason.STALLED, ((SessionClosedException) error.getCause()).reason());
        assertEquals(0, runtime.largeMessagesReceiving());
    }

    @Test
    void anIncomingLargeArgumentWhoseDataStopsDisconnectsWithStalled() throws Exception {
        progressTimeoutMillis = 1_000;
        connect();
        peer.send(MessageTypes.MESSAGE_START, array(4, 0, MessageTypes.INVOCATION, 200_000));
        awaitLargeMessages(1);
        // The stall must be the stream's, not the handshake write's (see the test above).
        ManualTimers.awaitWriterIdle(session);
        timers.advance(10_000);
        assertEquals(DisconnectReason.STALLED, closedWith());
    }

    @Test
    void progressIsMeasuredFromTheLastChunk() throws Exception {
        progressTimeoutMillis = 9_500;
        connect();
        CompletableFuture<byte[]> call = runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        peer.expect(MessageTypes.INVOCATION);
        CompletableFuture<Void> barrier = runtime.invokeTask(2, ArgumentWriter.NONE, null);
        peer.expect(MessageTypes.INVOCATION);
        peer.send(MessageTypes.MESSAGE_START, array(1, 1, MessageTypes.INVOCATION_RESULT, 200_000));
        awaitLargeMessages(1);
        timers.advance(900);
        peer.sendChunk(1, 1, new byte[1000], 0, 1000);
        // Frames are handled in order: once the barrier's result is in, so is the chunk.
        peer.send(MessageTypes.INVOCATION_RESULT, array(2, 1));
        await(barrier);
        // The barrier invocation was written at 0 s: a tick at 10 s that still saw the write in progress would find
        // it stalled.
        ManualTimers.awaitWriterIdle(session);

        // The tick at 10 s: 9.1 s since the chunk, under the 9.5 s timeout (10 s since the start would be over it).
        timers.advance(9_100);
        assertEquals(MessageTypes.PING, peer.server().readByte());
        assertTrue(session.isConnected());
        assertFalse(call.isDone());
        // Likewise the ping, written at 10 s: the stall the next tick finds must be the stream's.
        ManualTimers.awaitWriterIdle(session);

        // The next tick (20 s) finds the stream stalled.
        timers.advance(10_000);
        assertEquals(DisconnectReason.STALLED, closedWith());
        assertInstanceOf(CancellationException.class, failure(call));
    }
}
