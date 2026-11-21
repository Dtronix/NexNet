package com.dtronix.nexnet.invocation;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRemoteInvocationException;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.NexusUnauthorizedException;
import com.dtronix.nexnet.serialization.GoldenFiles;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Jitter;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import java.util.stream.Stream;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;
import org.junit.jupiter.api.Timeout;

/**
 * Replays the golden vectors of the invocation messages (K26) through the real {@link InvocationRuntime} over a
 * {@link NexusSession}: what the client receives is sent by a scripted server and observed in what the runtime does
 * (the method it runs, the call it completes, the credit it grants); what the client sends is produced by the runtime
 * and compared byte for byte. Invocation ids are allocated from 1, so a vector's id is reached by holding the ids
 * below it with outstanding calls.
 *
 * <p>Directions the client does not have are not replayed: it never sends {@code Unauthorized} results or error
 * details holding both a code and a type, or none of the three fields (those vectors are decoded only), and the
 * control messages it sends ({@code InvocationCancellation}, {@code InvocationCredit}, {@code MessageAbort}) are
 * encoded by the session's {@code ControlSender} ({@code session.GoldenSessionVectorsTest}). A reject vector of
 * {@code RemoteErrorDetail} is refused by dropping the detail: the result is an exception without one.
 */
@Timeout(120)
class GoldenInvocationVectorsTest {
    static final int CLIENT_HASH = -987654321;
    /** The client nexus has every method the vectors invoke; the test's own calls and invocations use 1 and 2. */
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {1, 2, 4, 300, 65535});
    static final byte[] SMALL_ARGS = {(byte) 0x91, 0x05};
    /** The invocation id of the InvocationResult reject vectors. */
    static final int REJECT_RESULT_ID = 3;

    @TestFactory
    Stream<DynamicTest> replay() {
        return GoldenFiles.of(GoldenFiles.INVOCATION_TYPES).stream()
            .map(v -> DynamicTest.dynamicTest(v.toString(), () -> replay(v)));
    }

    private static void replay(GoldenFiles.Vector v) throws Exception {
        boolean both = v.mode.equals("both");
        switch (v.type) {
            case "Invocation":
                if (v.isReject()) {
                    reject(v, MessageTypes.INVOCATION);
                } else {
                    decodeInvocation(v);
                    if (both) {
                        encodeInvocation(v);
                    }
                }
                break;
            case "InvocationResult":
                if (v.isReject()) {
                    rejectResult(v);
                } else {
                    decodeResult(v);
                    if (both) {
                        encodeResult(v);
                    }
                }
                break;
            case "InvocationCancellation":
                if (v.isReject()) {
                    reject(v, MessageTypes.INVOCATION_CANCELLATION);
                } else {
                    decodeCancellation(v);
                }
                break;
            case "InvocationCredit":
                if (v.isReject()) {
                    reject(v, MessageTypes.INVOCATION_CREDIT);
                } else {
                    decodeCredit(v);
                }
                break;
            case "MessageStart":
                if (v.isReject()) {
                    reject(v, MessageTypes.MESSAGE_START);
                } else {
                    decodeStart(v);
                    if (both) {
                        encodeStart(v);
                    }
                }
                break;
            case "MessageAbort":
                if (v.isReject()) {
                    reject(v, MessageTypes.MESSAGE_ABORT);
                } else {
                    decodeAbort(v);
                }
                break;
            case "RemoteErrorDetail":
                remoteErrorDetail(v);
                break;
            default:
                fail("Not an invocation vector type: " + v.type);
        }
    }

    /** A message the runtime must refuse: the session closes with {@code ProtocolError}. */
    private static void reject(GoldenFiles.Vector v, int type) throws Exception {
        try (Fixture f = new Fixture(32)) {
            f.peer.send(type, v.bytes());
            assertEquals(DisconnectReason.PROTOCOL_ERROR, f.closedWith(), "refused");
        }
    }

    // ------------------------------------------------------------------ Invocation

    /** {@code [invocationId, methodId, flags, arguments]} from the server: the method runs with the arguments. */
    private static void decodeInvocation(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int methodId = GoldenFiles.int32(value.get(1));
        int flags = GoldenFiles.int32(value.get(2));
        try (Fixture f = new Fixture(32)) {
            CompletableFuture<byte[]> arguments = new CompletableFuture<>();
            f.nexus.methods.put(methodId, (args, context) -> {
                arguments.complete(args);
                return CompletableFuture.completedFuture(null);
            });
            f.peer.send(MessageTypes.INVOCATION, v.bytes());
            assertArrayEquals(GoldenFiles.bytes(value.get(3)), arguments.get(5, TimeUnit.SECONDS), "arguments");
            if ((flags & InvocationRuntime.FLAG_IGNORE_RETURN) == 0) {
                assertEquals(id, idOf(f.peer.expect(MessageTypes.INVOCATION_RESULT).body), "the result's id");
            }
        }
    }

    /** The invocation the runtime sends for the vector's call. */
    private static void encodeInvocation(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int methodId = GoldenFiles.int32(value.get(1));
        int flags = GoldenFiles.int32(value.get(2));
        byte[] arguments = GoldenFiles.bytes(value.get(3));
        try (Fixture f = new Fixture(32)) {
            if (flags == 0) {
                f.holdIds(id - 1);
                boolean none = arguments.length == 1 && arguments[0] == (byte) 0x90;
                // A method without serialized arguments writes nothing; the runtime sends the empty array.
                f.runtime.invoke(methodId, none ? writer -> { } : TestCodec.args(arguments), TestCodec.BYTES, null);
            } else if (flags == InvocationRuntime.FLAG_IGNORE_RETURN) {
                f.runtime.invokeVoid(methodId, TestCodec.args(arguments));
            } else {
                // Pipe, channel and collection invocations (PipeManager): IgnoreReturn | DuplexPipe.
                f.runtime.invokeIgnoreReturn(methodId, flags, arguments);
            }
            assertEquals(GoldenFiles.hex(v.bytes()), GoldenFiles.hex(f.peer.expect(MessageTypes.INVOCATION).body),
                "encode");
        }
    }

    // ------------------------------------------------------------------ InvocationResult

    /** {@code [invocationId, state, result?]} for an outstanding call: the call completes accordingly. */
    private static void decodeResult(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int state = GoldenFiles.int32(value.get(1));
        try (Fixture f = new Fixture(Math.max(32, id))) {
            CompletableFuture<byte[]> call = f.callWithId(id);
            f.peer.send(MessageTypes.INVOCATION_RESULT, v.bytes());
            switch (state) {
                case 1: {
                    byte[] result = call.get(5, TimeUnit.SECONDS);
                    if (value.size() == 2) {
                        assertNull(result, "no result value");
                    } else {
                        Object json = value.get(2);
                        assertArrayEquals(json == null ? new byte[] {(byte) 0xC0} : GoldenFiles.bytes(json), result);
                    }
                    break;
                }
                case 2: {
                    NexusRemoteInvocationException e =
                        assertInstanceOf(NexusRemoteInvocationException.class, failure(call));
                    assertDetail(GoldenFiles.bytes(value.get(2)), e);
                    break;
                }
                case 3:
                    assertInstanceOf(NexusUnauthorizedException.class, failure(call));
                    break;
                case 4:
                    assertInstanceOf(CancellationException.class, failure(call));
                    break;
                default:
                    fail("Unknown result state " + state);
            }
            assertFalse(f.session.isClosing(), "still connected");
        }
    }

    /** The detail fields, read with the serialization codec's reader. */
    private static void assertDetail(byte[] detail, NexusRemoteInvocationException e) {
        MsgPackReader r = new MsgPackReader(detail, 0, detail.length);
        assertEquals(3, r.readArrayHeader());
        Integer code = r.tryReadNil() ? null : r.readInt();
        assertEquals(code, e.remoteErrorCode(), "code");
        assertEquals(r.readString(), e.remoteExceptionType(), "type");
        assertEquals(r.readString(), e.remoteMessage(), "message");
    }

    /** The result the runtime sends for an invocation from the server. */
    private static void encodeResult(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int state = GoldenFiles.int32(value.get(1));
        if (state == 2 || state == 3) {
            // The client never sends Unauthorized, nor a detail with both a code and a type: decoded only.
            return;
        }
        try (Fixture f = new Fixture(32)) {
            f.nexus.methods.put(2, (args, context) -> {
                if (state == 4) {
                    CompletableFuture<Void> result = new CompletableFuture<>();
                    context.cancellationToken().register(() -> result.cancel(true));
                    return result;
                }
                if (value.size() == 2) {
                    return CompletableFuture.completedFuture(null);
                }
                Object json = value.get(2);
                byte[] result = json == null ? new byte[] {(byte) 0xC0} : GoldenFiles.bytes(json);
                return context.result(CompletableFuture.completedFuture(result), TestCodec.BYTES);
            });
            f.peer.send(MessageTypes.INVOCATION, Peer.array(id, 2, 0, SMALL_ARGS));
            if (state == 4) {
                f.peer.send(MessageTypes.INVOCATION_CANCELLATION, Peer.array(id));
            }
            assertEquals(GoldenFiles.hex(v.bytes()),
                GoldenFiles.hex(f.peer.expect(MessageTypes.INVOCATION_RESULT).body), "encode");
        }
    }

    /** An InvocationResult reject vector, for an outstanding call with its id: the session closes. */
    private static void rejectResult(GoldenFiles.Vector v) throws Exception {
        try (Fixture f = new Fixture(32)) {
            CompletableFuture<byte[]> call = f.callWithId(REJECT_RESULT_ID);
            f.peer.send(MessageTypes.INVOCATION_RESULT, v.bytes());
            assertEquals(DisconnectReason.PROTOCOL_ERROR, f.closedWith(), "refused");
            assertInstanceOf(CancellationException.class, failure(call), "the call ends with the session");
        }
    }

    // ------------------------------------------------------------------ control messages

    /** {@code [invocationId]} for a running invocation: its token is canceled and it ends Canceled. */
    private static void decodeCancellation(GoldenFiles.Vector v) throws Exception {
        int id = GoldenFiles.int32(v.list().get(0));
        try (Fixture f = new Fixture(32)) {
            f.nexus.methods.put(2, (args, context) -> {
                CompletableFuture<Void> result = new CompletableFuture<>();
                context.cancellationToken().register(() -> result.cancel(true));
                return result;
            });
            f.peer.send(MessageTypes.INVOCATION, Peer.array(id, 2, 0, SMALL_ARGS));
            f.peer.send(MessageTypes.INVOCATION_CANCELLATION, v.bytes());
            byte[] result = f.peer.expect(MessageTypes.INVOCATION_RESULT).body;
            WireIn r = new WireIn(result, 0, result.length);
            assertEquals(2, r.arrayHeader());
            assertEquals(id, r.readUnsigned(0xFFFF), "id");
            assertEquals(4, r.readUnsigned(0xFF), "Canceled");
        }
    }

    /**
     * {@code [credits]} when exactly that many were taken: a call waiting for credit is sent, and the server did not
     * return more than it was given.
     */
    private static void decodeCredit(GoldenFiles.Vector v) throws Exception {
        int credits = GoldenFiles.int32(v.list().get(0));
        try (Fixture f = new Fixture(credits)) {
            f.holdIds(credits);
            CompletableFuture<byte[]> waiting = f.call();
            f.peer.send(MessageTypes.INVOCATION_CREDIT, v.bytes());
            f.peer.expect(MessageTypes.INVOCATION);
            assertFalse(waiting.isDone(), "sent, waiting for its result");
            assertFalse(f.session.isClosing(), "still connected");
        }
    }

    // ------------------------------------------------------------------ large messages

    /**
     * {@code [invocationId, kind, innerType, totalLength]} from the server, followed by the chunks of a body of that
     * length: the invocation runs (kind 0) or the call completes (kind 1) with it.
     */
    private static void decodeStart(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int kind = GoldenFiles.int32(value.get(1));
        int total = GoldenFiles.int32(value.get(3));
        try (Fixture f = new Fixture(32)) {
            if (kind == 0) {
                assertEquals(MessageTypes.INVOCATION, GoldenFiles.int32(value.get(2)), "inner type");
                byte[] arguments = Peer.bin(total - Peer.array(id, 1, 0, new byte[0]).length - 5);
                byte[] body = Peer.array(id, 1, 0, arguments);
                assertEquals(total, body.length);
                CompletableFuture<byte[]> received = new CompletableFuture<>();
                f.nexus.methods.put(1, (args, context) -> {
                    received.complete(args);
                    return CompletableFuture.completedFuture(null);
                });
                f.peer.send(MessageTypes.MESSAGE_START, v.bytes());
                chunks(f, id, kind, body);
                assertArrayEquals(arguments, received.get(5, TimeUnit.SECONDS), "arguments");
                assertEquals(id, idOf(f.peer.expect(MessageTypes.INVOCATION_RESULT).body), "the result's id");
            } else {
                assertEquals(MessageTypes.INVOCATION_RESULT, GoldenFiles.int32(value.get(2)), "inner type");
                CompletableFuture<byte[]> call = f.callWithId(id);
                byte[] result = Peer.bin(total - Peer.array(id, 1, new byte[0]).length - 5);
                byte[] body = Peer.array(id, 1, result);
                assertEquals(total, body.length);
                f.peer.send(MessageTypes.MESSAGE_START, v.bytes());
                chunks(f, id, kind, body);
                assertArrayEquals(result, call.get(5, TimeUnit.SECONDS), "result");
            }
        }
    }

    private static void chunks(Fixture f, int id, int kind, byte[] body) {
        for (int offset = 0; offset < body.length; offset += 65535) {
            f.peer.sendChunk(id, kind, body, offset, Math.min(65535, body.length - offset));
        }
    }

    /** The MessageStart the runtime sends for a large argument (kind 0) or result (kind 1) of that length. */
    private static void encodeStart(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int kind = GoldenFiles.int32(value.get(1));
        int total = GoldenFiles.int32(value.get(3));
        try (Fixture f = new Fixture(32)) {
            if (kind == 0) {
                f.holdIds(id - 1);
                byte[] arguments = Peer.bin(total - Peer.array(id, 1, 0, new byte[0]).length - 5);
                f.runtime.invoke(1, TestCodec.args(arguments), TestCodec.BYTES, null);
            } else {
                byte[] result = Peer.bin(total - Peer.array(id, 1, new byte[0]).length - 5);
                f.nexus.methods.put(2, (args, context) ->
                    context.result(CompletableFuture.completedFuture(result), TestCodec.BYTES));
                f.peer.send(MessageTypes.INVOCATION, Peer.array(id, 2, 0, SMALL_ARGS));
            }
            assertEquals(GoldenFiles.hex(v.bytes()), GoldenFiles.hex(f.peer.expect(MessageTypes.MESSAGE_START).body),
                "encode");
        }
    }

    /**
     * {@code [invocationId, kind, reason]} from the sender of a stream the client is receiving: {@code Cancelled}
     * drops the partial message; {@code Ack} ends a stream the client refused ({@code Busy}, its budget full).
     */
    private static void decodeAbort(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int id = GoldenFiles.int32(value.get(0));
        int kind = GoldenFiles.int32(value.get(1));
        try (Fixture f = new Fixture(32)) {
            if (kind == 1) {
                f.callWithId(id);
            }
            boolean ack = GoldenFiles.int32(value.get(2)) == 3;
            SessionBudget budget = f.runtime.budget();
            if (ack) {
                assertTrue(budget.tryReserve(budget.limit() - budget.reserved()), "the budget is full");
            }
            int innerType = kind == 0 ? MessageTypes.INVOCATION : MessageTypes.INVOCATION_RESULT;
            f.peer.send(MessageTypes.MESSAGE_START, Peer.array(id, kind, innerType, 100_000));
            f.peer.sendChunk(id, kind, new byte[100], 0, 100);
            waitFor(() -> f.runtime.largeMessagesReceiving() == 1);
            if (ack) {
                byte[] busy = f.peer.expect(MessageTypes.MESSAGE_ABORT).body;
                WireIn r = new WireIn(busy, 0, busy.length);
                assertEquals(3, r.arrayHeader());
                assertEquals(id, r.readUnsigned(0xFFFF), "id");
                assertEquals(kind, r.readUnsigned(0xFF), "kind");
                assertEquals(0, r.readUnsigned(0xFF), "Busy");
            }
            f.peer.send(MessageTypes.MESSAGE_ABORT, v.bytes());
            waitFor(() -> f.runtime.largeMessagesReceiving() == 0);
            assertFalse(f.session.isClosing(), "still connected");
        }
    }

    // ------------------------------------------------------------------ RemoteErrorDetail

    /** {@code [code, type, message]}: read by the runtime into the exception; a malformed detail is dropped. */
    private static void remoteErrorDetail(GoldenFiles.Vector v) {
        byte[] bytes = v.bytes();
        NexusRemoteInvocationException e = InvocationRuntime.remoteError(bytes, 0, bytes.length);
        if (v.isReject()) {
            assertNull(e.remoteErrorCode(), "dropped");
            assertNull(e.remoteExceptionType(), "dropped");
            assertNull(e.remoteMessage(), "dropped");
            return;
        }
        List<Object> value = v.list();
        assertEquals(value.get(0) == null ? null : GoldenFiles.int32(value.get(0)), e.remoteErrorCode(), "code");
        assertEquals(value.get(1), e.remoteExceptionType(), "type");
        assertEquals(value.get(2), e.remoteMessage(), "message");
    }

    // ------------------------------------------------------------------ fixture

    private static int idOf(byte[] body) {
        WireIn r = new WireIn(body, 0, body.length);
        r.arrayHeader();
        return r.readUnsigned(0xFFFF);
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

    private static void waitFor(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not met.");
            }
            Thread.sleep(2);
        }
    }

    private static Thread daemon(Runnable runnable, String name) {
        Thread thread = new Thread(runnable, name);
        thread.setDaemon(true);
        return thread;
    }

    /** A connected session and invocation runtime, with a scripted server. */
    private static final class Fixture implements AutoCloseable {
        final ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> daemon(r, "golden-callbacks"));
        final ExecutorService invocations = Jitter.executor(Executors.newCachedThreadPool(r -> daemon(r, "golden-invocations")));
        final InvocationRuntimeTest.TestNexus nexus = new InvocationRuntimeTest.TestNexus();
        final ManualTimers timers = new ManualTimers();
        final NexusSession session;
        final InvocationRuntime runtime;
        final Peer peer;

        /** Connects; the server grants {@code serverCredits} invocation credits. */
        Fixture(int serverCredits) throws Exception {
            LoopbackTransport transport = new LoopbackTransport();
            session = NexusSession.builder(transport, HANDSHAKE).name("golden").timers(timers)
                .options(SessionOptions.defaults().withDisconnectDelayMillis(0)).build();
            runtime = InvocationRuntime.builder(session, HANDSHAKE, nexus).codec(TestCodec.INSTANCE)
                .callbackExecutor(callbacks).invocationExecutor(invocations).build();
            session.setMessageHandler(runtime);
            CompletableFuture<Void> ready = session.start();
            FakeServer server = transport.server();
            peer = new Peer(server);
            server.readHandshake();
            server.write(FakeServer.PREAMBLE);
            server.write(FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, serverCredits));
            ready.get(5, TimeUnit.SECONDS);
            runtime.connected();
            runtime.startDispatching();
        }

        CompletableFuture<byte[]> call() {
            return runtime.invoke(1, TestCodec.args(SMALL_ARGS), TestCodec.BYTES, null);
        }

        /** Holds the next {@code count} invocation ids with calls left outstanding, and reads their invocations. */
        void holdIds(int count) {
            for (int i = 0; i < count; i++) {
                call();
            }
            for (int i = 0; i < count; i++) {
                peer.expect(MessageTypes.INVOCATION);
            }
        }

        /** A call sent with invocation id {@code id} (ids are allocated from 1). */
        CompletableFuture<byte[]> callWithId(int id) {
            holdIds(id - 1);
            CompletableFuture<byte[]> call = call();
            assertEquals(id, idOf(peer.expect(MessageTypes.INVOCATION).body), "invocation id");
            return call;
        }

        DisconnectReason closedWith() throws Exception {
            return session.closedInternal().get(5, TimeUnit.SECONDS);
        }

        @Override
        public void close() {
            session.abort(DisconnectReason.GRACEFUL);
            boolean ended;
            try {
                ended = session.awaitThreads();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new AssertionError(e);
            }
            // Queued completions run first (they settle the calls the close failed), then the pools end.
            callbacks.shutdown();
            invocations.shutdown();
            try {
                assertTrue(callbacks.awaitTermination(5, TimeUnit.SECONDS), "callbacks ended");
                assertTrue(invocations.awaitTermination(5, TimeUnit.SECONDS), "invocations ended");
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new AssertionError(e);
            }
            assertTrue(ended, "threads ended");
        }
    }
}
