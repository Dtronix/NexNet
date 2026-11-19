package com.dtronix.nexnet.session;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.serialization.GoldenFiles;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.function.UnaryOperator;
import java.util.stream.Stream;
import org.junit.jupiter.api.Assumptions;
import org.junit.jupiter.api.DynamicTest;
import org.junit.jupiter.api.TestFactory;
import org.junit.jupiter.api.Timeout;

/**
 * Replays the golden vectors of the session layer (K26) through the real {@link NexusSession} and
 * {@link ControlSender}: the preamble and the greetings the session writes are compared byte for byte, every frame
 * the server could send is read by the session's reader loop, the {@code ServerGreeting} vectors complete (or are
 * refused by) the handshake, and the control messages the session sends on its own are built by its
 * {@link ControlSender}. Frame bodies are left to their owners: the invocation runtime
 * ({@code invocation.GoldenInvocationVectorsTest}), the pipe runtime ({@code pipes.GoldenPipeVectorsTest}).
 *
 * <p>The client only sends {@code ClientGreeting}; its decode and reject vectors are for servers and are skipped
 * (the client refuses any {@code ClientGreeting} it receives). It only receives {@code ServerGreeting}, so those
 * vectors are decoded and not encoded.
 */
@Timeout(60)
class GoldenSessionVectorsTest {
    /** The golden ServerGreeting frame the connected fixtures use: version (the client nexus hash) 1. */
    private static final GoldenFiles.Vector GREETING = GoldenFiles.byId("frame.server-greeting");
    private static final byte[] PREAMBLE = GoldenFiles.byId("preamble").bytes();
    /** A frame the reader hands to the message handler, written after a header-only frame as a barrier. */
    private static final GoldenFiles.Vector BARRIER = GoldenFiles.byId("frame.invocation-credit");

    /** Rejects of the control messages are refused by the invocation runtime, which reads them. */
    private static final List<String> CONTROL = Arrays.asList("InvocationCancellation", "InvocationCredit",
        "MessageAbort");

    @TestFactory
    Stream<DynamicTest> replay() {
        List<DynamicTest> tests = new ArrayList<>();
        for (GoldenFiles.Vector v : GoldenFiles.of(GoldenFiles.SESSION_TYPES)) {
            if (CONTROL.contains(v.type) && v.isReject()) {
                continue;
            }
            tests.add(DynamicTest.dynamicTest(v.toString(), () -> replay(v)));
        }
        return tests.stream();
    }

    private static void replay(GoldenFiles.Vector v) throws Exception {
        switch (v.type) {
            case "ClientGreeting":
            case "ClientGreetingReconnection":
                clientGreeting(v);
                break;
            case "ServerGreeting":
                serverGreeting(v.isReject(), v.list(), frame(MessageTypes.SERVER_GREETING, v.bytes()));
                break;
            case "InvocationCancellation":
            case "InvocationCredit":
            case "MessageAbort":
                control(v);
                break;
            case "preamble":
                preamble(v);
                break;
            case "frame":
                frame(v);
                break;
            default:
                fail("Not a session vector type: " + v.type);
        }
    }

    // ------------------------------------------------------------------ greetings

    private static void clientGreeting(GoldenFiles.Vector v) throws Exception {
        Assumptions.assumeTrue(v.mode.equals("both"),
            "server-side: the client never decodes a ClientGreeting (its reader refuses one)");
        int type = v.type.equals("ClientGreeting") ? MessageTypes.CLIENT_GREETING
            : MessageTypes.CLIENT_GREETING_RECONNECTION;
        try (Fixture f = new Fixture()) {
            assertArrayEquals(frame(type, v.bytes()), f.startGreeting(v.list(), type).server().readHandshake(),
                "the greeting the session writes");
        }
    }

    /** {@code [version, serverNexusHash, clientNexusHash, token, maxMessageSize, invocationCredits]}. */
    private static HandshakeDescriptor greetingHandshake(List<Object> value) {
        return new HandshakeDescriptor(NexusRuntime.VERSION, (String) value.get(0), GoldenFiles.int32(value.get(1)),
            GoldenFiles.int32(value.get(2)), new int[0]);
    }

    /**
     * Connects with the ServerGreeting frame {@code greeting}: its fields reach the session (decode), or the session
     * closes with {@code ProtocolError} (reject). {@code value}: {@code [version, clientId, maxMessageSize,
     * invocationCredits, idleTimeoutMs]}.
     */
    private static void serverGreeting(boolean reject, List<Object> value, byte[] greeting) throws Exception {
        try (Fixture f = new Fixture()) {
            int version = reject ? 1 : GoldenFiles.int32(value.get(0));
            NexusSession session = f.start(handshake(version), b -> b);
            f.server().readHandshake();
            f.server().write(PREAMBLE);
            f.server().write(greeting);
            if (reject) {
                assertEquals(DisconnectReason.PROTOCOL_ERROR, f.closedWith(), "refused");
                assertTrue(session.ready().isCompletedExceptionally(), "the handshake failed");
                return;
            }
            session.ready().get(5, TimeUnit.SECONDS);
            assertEquals(GoldenFiles.integer(value.get(1)), session.id(), "clientId");
            assertEquals(GoldenFiles.int32(value.get(2)), session.peerMaxMessageSize(), "maxMessageSize");
            assertEquals(GoldenFiles.int32(value.get(3)), session.peerInvocationCredits(), "invocationCredits");
            long idle = GoldenFiles.integer(value.get(4));
            assertEquals(idle > 0 ? idle : -1, session.advertisedIdleTimeoutMillis(), "idleTimeoutMs");
        }
    }

    private static HandshakeDescriptor handshake(int clientNexusHash) {
        return new HandshakeDescriptor(NexusRuntime.VERSION, null, 0, clientNexusHash, new int[0]);
    }

    // ------------------------------------------------------------------ control messages

    /** The control messages the session sends on its own: built by its {@link ControlSender}. */
    private static void control(GoldenFiles.Vector v) {
        List<Object> value = v.list();
        ControlSender sender = new ControlSender(() -> { }, 2);
        int type;
        switch (v.type) {
            case "InvocationCancellation":
                type = MessageTypes.INVOCATION_CANCELLATION;
                sender.requestInvocationCancellation(GoldenFiles.int32(value.get(0)));
                break;
            case "InvocationCredit":
                type = MessageTypes.INVOCATION_CREDIT;
                sender.returnInvocationCredit(GoldenFiles.int32(value.get(0)));
                break;
            default:
                type = MessageTypes.MESSAGE_ABORT;
                sender.requestAbort(GoldenFiles.int32(value.get(0)), GoldenFiles.int32(value.get(1)),
                    GoldenFiles.int32(value.get(2)));
                break;
        }
        List<Frame> frames = new ArrayList<>();
        sender.drainTo(frames);
        assertEquals(1, frames.size(), "frames");
        assertEquals(GoldenFiles.hex(frame(type, v.bytes())), GoldenFiles.hex(frames.get(0).bytes()), "encode");
    }

    // ------------------------------------------------------------------ frames

    private static void preamble(GoldenFiles.Vector v) throws Exception {
        try (Fixture f = new Fixture()) {
            f.start(handshake(1), b -> b);
            assertArrayEquals(v.bytes(), f.server().read(v.bytes().length), "the preamble the session writes");
        }
        try (Fixture f = new Fixture()) {
            NexusSession session = f.start(handshake(1), b -> b);
            f.server().readHandshake();
            f.server().write(v.bytes());
            f.server().write(GREETING.bytes());
            session.ready().get(5, TimeUnit.SECONDS);
        }
    }

    private static void frame(GoldenFiles.Vector v) throws Exception {
        List<Object> value = v.list();
        int type = GoldenFiles.int32(value.get(0));
        byte[] bytes = v.bytes();
        if (type == MessageTypes.PING || MessageTypes.isDisconnect(type)) {
            assertEquals(GoldenFiles.hex(bytes), GoldenFiles.hex(Frame.headerOnly(type).bytes()), "encode");
            headerOnly(type, bytes);
            return;
        }
        if (type == MessageTypes.CLIENT_GREETING) {
            // Sent only: the session writes it.
            try (Fixture f = new Fixture()) {
                assertArrayEquals(bytes, f.startGreeting(GoldenFiles.list(value.get(1)), type).server().readHandshake());
            }
            return;
        }
        if (type == MessageTypes.SERVER_GREETING) {
            // Received only: the session reads it.
            serverGreeting(false, GoldenFiles.list(value.get(1)), bytes);
            return;
        }
        int postLength = MessageTypes.postHeaderLength(type);
        int postHeader = 0;
        byte[] body = Arrays.copyOfRange(bytes, 3 + postLength, bytes.length);
        if (type == MessageTypes.DUPLEX_PIPE_WRITE || type == MessageTypes.PIPE_WINDOW_UPDATE) {
            postHeader = GoldenFiles.int32(value.get(1)) | (GoldenFiles.int32(value.get(2)) << 8);
        } else if (type == MessageTypes.MESSAGE_CHUNK) {
            postHeader = GoldenFiles.int32(value.get(1)) | (GoldenFiles.int32(value.get(2)) << 16);
        }
        if (type == MessageTypes.DUPLEX_PIPE_WRITE || type == MessageTypes.MESSAGE_CHUNK) {
            assertArrayEquals(GoldenFiles.bytes(value.get(3)), body, "the raw body");
        }
        assertEquals(GoldenFiles.hex(bytes), GoldenFiles.hex(Frame.of(type, postHeader, body, 0, body.length).bytes()),
            "encode");
        try (Fixture f = new Fixture()) {
            f.connect();
            f.server().write(bytes);
            InboundFrame frame = f.received.poll(5, TimeUnit.SECONDS);
            assertTrue(frame != null, "the reader handed the frame on");
            assertEquals(type, frame.type(), "type");
            assertEquals(postHeader, frame.postHeader(), "post-header");
            if (type == MessageTypes.DUPLEX_PIPE_WRITE || type == MessageTypes.PIPE_WINDOW_UPDATE) {
                assertEquals(postHeader, frame.pipeId(), "pipe id");
            } else if (type == MessageTypes.MESSAGE_CHUNK) {
                assertEquals(GoldenFiles.int32(value.get(1)), frame.chunkInvocationId(), "invocation id");
                assertEquals(GoldenFiles.int32(value.get(2)), frame.chunkKind(), "kind");
            }
            assertArrayEquals(body, frame.body(), "body");
        }
    }

    /** A Ping is consumed by the reader; a disconnect code closes the session with its reason. */
    private static void headerOnly(int type, byte[] bytes) throws Exception {
        try (Fixture f = new Fixture()) {
            NexusSession session = f.connect();
            if (type == MessageTypes.PING) {
                session.controlSender().requestPing();
                assertEquals(type, f.server().readByte(), "the Ping the session writes");
                f.server().write(bytes);
                f.server().write(BARRIER.bytes());
                InboundFrame next = f.received.poll(5, TimeUnit.SECONDS);
                assertTrue(next != null && next.type() == MessageTypes.INVOCATION_CREDIT,
                    "the frame after the Ping is read");
                assertTrue(session.isConnected(), "still connected");
            } else {
                f.server().write(bytes);
                assertEquals(DisconnectReason.fromCode(type), f.closedWith(), "the disconnect reason");
            }
        }
    }

    /** {@code type}, the little-endian body length, then the body. */
    private static byte[] frame(int type, byte[] body) {
        byte[] frame = new byte[3 + body.length];
        frame[0] = (byte) type;
        frame[1] = (byte) body.length;
        frame[2] = (byte) (body.length >>> 8);
        System.arraycopy(body, 0, frame, 3, body.length);
        return frame;
    }

    /** One session over a loopback transport, whose received messages are queued. */
    private static final class Fixture implements AutoCloseable {
        final ManualTimers timers = new ManualTimers();
        final LoopbackTransport transport = new LoopbackTransport();
        final BlockingQueue<InboundFrame> received = new LinkedBlockingQueue<>();
        NexusSession session;

        NexusSession start(HandshakeDescriptor handshake, UnaryOperator<NexusSession.Builder> configure) {
            NexusSession.Builder builder = NexusSession.builder(transport, handshake).name("golden").timers(timers)
                .options(SessionOptions.defaults().withDisconnectDelayMillis(0));
            session = configure.apply(builder).build();
            session.setMessageHandler(received::add);
            session.start();
            return session;
        }

        /** Starts a session whose greeting carries {@code value}. */
        Fixture startGreeting(List<Object> value, int type) {
            start(greetingHandshake(value), b -> b
                .authenticationToken(GoldenFiles.bytes(value.get(3)))
                .reconnecting(type == MessageTypes.CLIENT_GREETING_RECONNECTION)
                .options(SessionOptions.defaults().withDisconnectDelayMillis(0)
                    .withMaxMessageSize(GoldenFiles.int32(value.get(4)))
                    .withInvocationCredits(GoldenFiles.int32(value.get(5)))));
            return this;
        }

        /** Starts a session and connects it with the golden ServerGreeting. */
        NexusSession connect() throws Exception {
            start(handshake(GoldenFiles.int32(GoldenFiles.list(GREETING.list().get(1)).get(0))), b -> b);
            server().readHandshake();
            server().write(PREAMBLE);
            server().write(GREETING.bytes());
            session.ready().get(5, TimeUnit.SECONDS);
            return session;
        }

        FakeServer server() {
            return transport.server();
        }

        DisconnectReason closedWith() throws InterruptedException, ExecutionException,
            java.util.concurrent.TimeoutException {
            return session.closedInternal().get(5, TimeUnit.SECONDS);
        }

        @Override
        public void close() {
            if (session == null) {
                return;
            }
            session.abort(DisconnectReason.GRACEFUL);
            boolean ended;
            try {
                ended = session.awaitThreads();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new AssertionError(e);
            }
            assertTrue(ended, "threads ended");
        }
    }
}
