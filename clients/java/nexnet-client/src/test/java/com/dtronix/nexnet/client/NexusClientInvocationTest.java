package com.dtronix.nexnet.client;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.invocation.InvocationCounters;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.invocation.RemoteExceptionDetail;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.GoldenFrames;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/** The invocation runtime as wired into {@link NexusClient} (K07). */
@Timeout(30)
class NexusClientInvocationTest {
    static final HandshakeDescriptor HANDSHAKE = new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789,
        NexusClientTest.CLIENT_HASH, new int[] {0});

    static final class TestProxy extends ProxyBase {
        TestProxy(ProxyInvoker invoker) {
            super(invoker);
        }

        CompletableFuture<Void> ping() {
            return invokeTask(1, ArgumentWriter.NONE, null);
        }

        void notifyServer() {
            invokeVoid(2, ArgumentWriter.NONE);
        }
    }

    static final class TestNexus extends ClientNexusBase<TestProxy> {
        final List<String> events = new CopyOnWriteArrayList<>();
        final CountDownLatch connectedGate = new CountDownLatch(1);

        @Override
        protected CompletableFuture<Void> onConnected(boolean isReconnected) {
            try {
                connectedGate.await(10, TimeUnit.SECONDS);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            events.add("connected");
            return CompletableFuture.completedFuture(null);
        }

        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            events.add("dispatch " + methodId);
            throw new IllegalArgumentException("No method " + methodId);
        }
    }

    private final TestNexus nexus = new TestNexus();
    private final List<LoopbackTransport> transports = new CopyOnWriteArrayList<>();
    private final List<NexusClient<TestProxy>> clients = new ArrayList<>();

    private final ManualTimers timers = new ManualTimers();

    private NexusClient<TestProxy> client() {
        return client(ClientConfig.tcp("h", 1).build());
    }

    private NexusClient<TestProxy> client(ClientConfig clientConfig) {
        NexusClient<TestProxy> client = NexusClient.create(clientConfig, HANDSHAKE, nexus,
            TestProxy::new, (config, timeout) -> {
                LoopbackTransport transport = new LoopbackTransport();
                transports.add(transport);
                return transport;
            }, timers, SessionOptions.defaults().withDisconnectDelayMillis(0));
        clients.add(client);
        return client;
    }

    @AfterEach
    void tearDown() {
        nexus.connectedGate.countDown();
        for (NexusClient<TestProxy> client : clients) {
            client.close();
        }
    }

    private FakeServer accept() throws InterruptedException {
        NexusClientTest.waitFor(() -> !transports.isEmpty());
        FakeServer server = transports.get(0).server();
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(GoldenFrames.get("server-greeting"));
        return server;
    }

    private static byte[] readFrame(FakeServer server) {
        return server.readFrame();
    }

    @Test
    void callsFailWhileNotConnected() throws Exception {
        NexusClient<TestProxy> client = client();
        ExecutionException e = assertThrows(ExecutionException.class,
            () -> client.proxy().ping().get(5, TimeUnit.SECONDS));
        assertInstanceOf(IllegalStateException.class, e.getCause());
        assertThrows(IllegalStateException.class, () -> client.proxy().notifyServer());
    }

    @Test
    void serverInvocationsRunOnlyAfterOnConnected() throws Exception {
        NexusClient<TestProxy> client = client();
        CompletableFuture<Void> connected = client.connect();
        FakeServer server = accept();
        // Invocation [id 7, method 0, flags 0, []] right behind the greeting.
        server.write(Frame.of(MessageTypes.INVOCATION, new byte[] {(byte) 0x94, 7, 0, 0, (byte) 0x90}).bytes());
        CompletableFuture<byte[]> result = CompletableFuture.supplyAsync(() -> readFrame(server));
        Thread.sleep(200);
        assertFalse(result.isDone(), "no result before onConnected completes");
        assertTrue(nexus.events.isEmpty());
        nexus.connectedGate.countDown();
        connected.get(5, TimeUnit.SECONDS);
        // The method failed, so the server gets an Exception result without detail; its credit came back first.
        byte[] frame = result.get(5, TimeUnit.SECONDS);
        if (frame[0] == MessageTypes.INVOCATION_CREDIT) {
            frame = server.readFrame();
        }
        assertArrayEquals(new byte[] {MessageTypes.INVOCATION_RESULT, 3, 0, (byte) 0x92, 7, 2}, frame);
        assertEquals("connected", nexus.events.get(0));
    }

    @Test
    void theInvocationRuntimeHandlesMessagesFromTheFirstFrame() throws Exception {
        NexusClient<TestProxy> client = client();
        nexus.connectedGate.countDown();
        CompletableFuture<Void> connected = client.connect();
        FakeServer server = accept();
        connected.get(5, TimeUnit.SECONDS);
        // Credit the client never took: a flow-control error from the invocation runtime, not an unhandled type.
        server.write(Frame.of(MessageTypes.INVOCATION_CREDIT, new byte[] {(byte) 0x91, 1}).bytes());
        assertEquals(DisconnectReason.FLOW_CONTROL.code(), server.readByte());
        client.disconnected().get(5, TimeUnit.SECONDS);
        ExecutionException e = assertThrows(ExecutionException.class,
            () -> client.proxy().ping().get(5, TimeUnit.SECONDS));
        assertInstanceOf(IllegalStateException.class, e.getCause());
    }

    @Test
    void invocationOptionsFromTheConfigReachTheRuntime() throws Exception {
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).invocationOptions(
            InvocationOptions.defaults().withRemoteExceptionDetail(RemoteExceptionDetail.TYPE_AND_MESSAGE)).build());
        nexus.connectedGate.countDown();
        CompletableFuture<Void> connected = client.connect();
        FakeServer server = accept();
        connected.get(5, TimeUnit.SECONDS);
        server.write(Frame.of(MessageTypes.INVOCATION, new byte[] {(byte) 0x94, 7, 0, 0, (byte) 0x90}).bytes());
        byte[] frame = server.readFrame();
        if (frame[0] == MessageTypes.INVOCATION_CREDIT) {
            frame = server.readFrame();
        }
        // An Exception result [7, 2, detail] that, unlike the default (None, [7, 2]), carries the type and message.
        assertEquals(MessageTypes.INVOCATION_RESULT, frame[0]);
        assertArrayEquals(new byte[] {(byte) 0x93, 7, 2, (byte) 0x93}, java.util.Arrays.copyOfRange(frame, 3, 7));
        String text = new String(frame, java.nio.charset.StandardCharsets.ISO_8859_1);
        assertTrue(text.contains("Exception"), text);
    }

    @Test
    void theProgressTimeoutFromTheConfigReachesTheSession() throws Exception {
        NexusClient<TestProxy> client = client(ClientConfig.tcp("h", 1).progressTimeoutMillis(1_000).build());
        nexus.connectedGate.countDown();
        CompletableFuture<Void> connected = client.connect();
        FakeServer server = accept();
        connected.get(5, TimeUnit.SECONDS);
        int before = InvocationCounters.largeMessages();
        // MessageStart [id 7, kind Argument, inner Invocation, 200000 bytes]; then no data.
        server.write(Frame.of(MessageTypes.MESSAGE_START, new byte[] {(byte) 0x94, 7, 0, MessageTypes.INVOCATION,
            (byte) 0xCE, 0, 3, 0x0D, 0x40}).bytes());
        NexusClientTest.waitFor(() -> InvocationCounters.largeMessages() > before);
        // The stall must be the stream's: a tick that still saw the handshake write in progress would find it stalled
        // and close without the disconnect byte.
        ManualTimers.awaitWriterIdle(client.session());
        timers.advance(10_000);
        assertEquals(DisconnectReason.STALLED.code(), server.readByte());
        client.disconnected().get(5, TimeUnit.SECONDS);
    }
}
