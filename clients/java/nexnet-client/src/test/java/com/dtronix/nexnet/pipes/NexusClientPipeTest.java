package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionClosedException;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.nio.ByteBuffer;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/**
 * Pipes as wired into {@link NexusClient}: created from the client, routed by the session's pipe runtime and ended
 * with the session. The proxy side of the invocation needs the MessagePack codec (K02), so the server answers the
 * pipe's open as if the invocation had been sent.
 */
@Timeout(30)
class NexusClientPipeTest {
    static final class Proxy extends ProxyBase {
        Proxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class Nexus extends ClientNexusBase<Proxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("No method " + methodId);
        }
    }

    private final List<LoopbackTransport> transports = new CopyOnWriteArrayList<>();
    private NexusClient<Proxy> client;
    private ServerPeer peer;

    @AfterEach
    void tearDown() {
        if (client != null) {
            client.close();
        }
        if (peer != null) {
            peer.close();
        }
    }

    private void connect() throws Exception {
        connect(ClientConfig.tcp("h", 1).build());
    }

    private void connect(ClientConfig config) throws Exception {
        client = NexusClient.create(config, PipeTestBase.HANDSHAKE, new Nexus(),
            Proxy::new, (c, timeout) -> {
                LoopbackTransport transport = new LoopbackTransport();
                transports.add(transport);
                return transport;
            });
        assertThrows(IllegalStateException.class, () -> client.createPipe(), "not connected");
        CompletableFuture<Void> connected = client.connect();
        PipeTestBase.waitFor(() -> !transports.isEmpty());
        FakeServer server = transports.get(0).server();
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(FakeServer.serverGreeting(PipeTestBase.CLIENT_HASH, 1, 4 * 1024 * 1024, 32));
        connected.get(5, TimeUnit.SECONDS);
        peer = new ServerPeer(server);
    }

    @Test
    void aPipeFromTheClientCarriesDataBothWaysAndEndsWithTheSession() throws Exception {
        connect();
        int openBefore = PipeCounters.openPipes();
        NexusDuplexPipe pipe = client.createPipe();
        assertEquals(openBefore + 1, PipeCounters.openPipes());
        int fullId = 1 | (5 << 8);
        peer.sendState(fullId, PipeWire.READY, 1000);
        pipe.ready().get(5, TimeUnit.SECONDS);
        assertEquals(fullId, pipe.id());
        assertEquals(PipeOptions.DEFAULT_PIPE_WINDOW, peer.take(MessageTypes.PIPE_WINDOW_UPDATE, fullId).window());

        pipe.write(ByteBuffer.wrap(PipeTestBase.bytes(300, 1))).get(5, TimeUnit.SECONDS);
        assertArrayEquals(PipeTestBase.bytes(300, 1), peer.awaitData(fullId, 300));
        peer.sendData(fullId, PipeTestBase.bytes(200, 2));
        ByteBuffer read = pipe.read().get(5, TimeUnit.SECONDS);
        assertEquals(200, read.remaining());

        CompletableFuture<ByteBuffer> pending = pipe.read();
        CompletableFuture<Void> waiting = pipe.write(ByteBuffer.wrap(new byte[2000]));
        client.disconnect().get(5, TimeUnit.SECONDS);
        assertNull(pending.get(5, TimeUnit.SECONDS));
        try {
            waiting.get(5, TimeUnit.SECONDS);
        } catch (java.util.concurrent.ExecutionException e) {
            assertInstanceOf(SessionClosedException.class, e.getCause());
        }
        pipe.completed().get(5, TimeUnit.SECONDS);
        PipeTestBase.waitFor(() -> PipeCounters.openPipes() == openBefore);
        assertThrows(IllegalStateException.class, () -> client.createPipe(), "not connected");
    }

    @Test
    void thePipeOptionsOfTheConfigReachTheSession() throws Exception {
        connect(ClientConfig.tcp("h", 1)
            .pipeOptions(PipeOptions.defaults().withMaxPipes(1).withPipeWindow(4096)).build());
        NexusDuplexPipe pipe = client.createPipe();
        assertThrows(NexusBusyException.class, () -> client.createPipe(), "maxPipes 1");
        int fullId = 1 | (5 << 8);
        peer.sendState(fullId, PipeWire.READY, 1000);
        pipe.ready().get(5, TimeUnit.SECONDS);
        assertEquals(4096, peer.take(MessageTypes.PIPE_WINDOW_UPDATE, fullId).window());
    }

    @Test
    void aChannelFromTheClientReadsItems() throws Exception {
        connect();
        NexusDuplexChannel<byte[]> channel = client.createChannel(TestCodec.BYTES);
        assertEquals(0, channel.pipe().id(), "unassigned until the server's Ready");
        int fullId = 1 | (6 << 8);
        peer.sendState(fullId, PipeWire.READY, 1000);
        // Items need the MessagePack codec (K02); the channel's open and end are what is checked here.
        NexusChannelReader<byte[]> reader = channel.reader().get(5, TimeUnit.SECONDS);
        assertEquals(fullId, channel.pipe().id());
        peer.sendState(fullId, PipeWire.COMPLETE, 0);
        assertFalse(reader.read().get(5, TimeUnit.SECONDS).isPresent());
        peer.expectState(fullId, PipeWire.COMPLETE, 0);
        int open = PipeCounters.openPipes();
        channel.close();
        PipeTestBase.waitFor(() -> PipeCounters.openPipes() == open - 1);
    }
}
