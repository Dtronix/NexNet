package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.diagnostics.TestClients;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Jitter;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import org.junit.jupiter.api.AfterEach;

/** A real session, invocation runtime and pipe runtime against a scripted server. */
abstract class PipeTestBase {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {0, 1, 2, 3});

    /** A client nexus method of the test nexus. */
    interface Method {
        CompletableFuture<Void> run(byte[] arguments, InvocationContext context);
    }

    static final class TestNexus extends ClientNexusBase<Object> {
        final Map<Integer, Method> methods = new ConcurrentHashMap<>();

        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            return methods.get(methodId).run(TestCodec.currentArguments(), context);
        }
    }

    final ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> daemon(r, "test-callbacks"));
    final ExecutorService invocations = Jitter.executor(Executors.newCachedThreadPool(r -> daemon(r, "test-invocations")));
    final TestNexus nexus = new TestNexus();
    final List<CountDownLatch> latches = new ArrayList<>();
    /** Results of client methods that run until the test ends; completed in {@link #tearDown()}. */
    final List<CompletableFuture<Void>> methodResults = new ArrayList<>();
    /** The session's clock: drives its keepalive tick and so the progress check. */
    final ManualTimers sessionTimers = new ManualTimers();
    /** The session's progress timeout, set before connecting. */
    int progressTimeoutMillis = 30_000;
    /** The session's ping interval, set before connecting; null derives it. */
    Integer pingIntervalMillis;
    NexusSession session;
    InvocationRuntime runtime;
    PipeManager pipes;
    ServerPeer peer;

    static Thread daemon(Runnable runnable, String name) {
        Thread thread = new Thread(runnable, name);
        thread.setDaemon(true);
        return thread;
    }

    void connect(PipeOptions options) throws Exception {
        connect(options, InvocationOptions.defaults(), SessionTimers.system(), SessionOptions.defaults());
    }

    void connect(PipeOptions options, InvocationOptions invocationOptions, SessionTimers pipeTimers,
                 SessionOptions sessionOptions) throws Exception {
        LoopbackTransport transport = new LoopbackTransport();
        FakeServer server = transport.server();
        session = NexusSession.builder(transport, HANDSHAKE).name("t").timers(sessionTimers)
            .progressTimeoutMillis(progressTimeoutMillis).pingIntervalMillis(pingIntervalMillis)
            .options(sessionOptions.withDisconnectDelayMillis(0).withInvocationCredits(2)).build();
        runtime = InvocationRuntime.builder(session, HANDSHAKE, nexus).options(invocationOptions)
            .codec(TestCodec.INSTANCE).callbackExecutor(callbacks).invocationExecutor(invocations).build();
        session.setMessageHandler(runtime);
        pipes = PipeManager.attach(runtime, callbacks, pipeTimers, options, null);
        CompletableFuture<Void> ready = session.start();
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, 32));
        ready.get(5, TimeUnit.SECONDS);
        peer = new ServerPeer(server);
        runtime.connected();
        runtime.startDispatching();
    }

    CountDownLatch latch() {
        CountDownLatch latch = new CountDownLatch(1);
        latches.add(latch);
        return latch;
    }

    /**
     * A result for a client method that keeps running until the test completes it, or until the test ends. Without
     * this the invocation stays running after the session is closed and the leak audit counts it.
     */
    CompletableFuture<Void> methodResult() {
        CompletableFuture<Void> result = new CompletableFuture<>();
        synchronized (methodResults) {
            methodResults.add(result);
        }
        return result;
    }

    @AfterEach
    void tearDown() throws Exception {
        for (CountDownLatch latch : latches) {
            latch.countDown();
        }
        if (session != null) {
            session.abort(DisconnectReason.GRACEFUL);
            assertTrue(session.awaitThreads(), "threads ended");
            waitFor(() -> pipes.openPipes() == 0);
        }
        synchronized (methodResults) {
            for (CompletableFuture<Void> result : methodResults) {
                result.complete(null);
            }
        }
        if (peer != null) {
            peer.close();
        }
        // Leak audit (D23): every invocation has finished and every counter is back to 0.
        TestClients.awaitIdle();
        callbacks.shutdownNow();
        invocations.shutdownNow();
    }

    /** The server invokes a client method: {@code [id, methodId, flags, arguments]}. */
    void invoke(int id, int methodId, int flags, byte... arguments) {
        byte[] body = new byte[4 + arguments.length];
        body[0] = (byte) 0x94;
        body[1] = (byte) id;
        body[2] = (byte) methodId;
        body[3] = (byte) flags;
        System.arraycopy(arguments, 0, body, 4, arguments.length);
        peer.send(MessageTypes.INVOCATION, body);
    }

    /** The server opens a pipe to client method {@code methodId} with its local id {@code serverId}. */
    void invokePipeMethod(int methodId, int serverId) {
        invoke(0, methodId, PipeWire.INVOCATION_FLAGS, (byte) 0x91, (byte) serverId);
    }

    static <T> T await(CompletableFuture<T> future) throws Exception {
        return future.get(5, TimeUnit.SECONDS);
    }

    static Throwable failure(CompletableFuture<?> future) throws Exception {
        try {
            future.get(5, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            return e.getCause();
        } catch (CancellationException e) {
            return e;
        }
        throw new AssertionError("The future completed normally.");
    }

    static void waitFor(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not met in time.");
            }
            Thread.sleep(5);
        }
    }

    /** A MessagePack bin32 value of {@code length} patterned bytes, seeded. */
    static byte[] bin(int length, int seed) {
        byte[] value = new byte[5 + length];
        value[0] = (byte) 0xC6;
        value[1] = (byte) (length >>> 24);
        value[2] = (byte) (length >>> 16);
        value[3] = (byte) (length >>> 8);
        value[4] = (byte) length;
        for (int i = 0; i < length; i++) {
            value[5 + i] = (byte) (i * 31 + seed);
        }
        return value;
    }

    /** Items framed as on a channel. */
    static byte[] framed(byte[]... items) {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        for (byte[] item : items) {
            byte[] prefix = new byte[5];
            int n = PipeWire.writePrefix(prefix, 0, item.length);
            out.write(prefix, 0, n);
            out.write(item, 0, item.length);
        }
        return out.toByteArray();
    }

    static byte[] bytes(int length, int seed) {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++) {
            data[i] = (byte) (i * 7 + seed);
        }
        return data;
    }
}
