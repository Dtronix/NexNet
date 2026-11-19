package com.dtronix.nexnet.transport;

import static com.dtronix.nexnet.transport.TestServer.frame;
import static com.dtronix.nexnet.transport.TestServer.readFrame;
import static com.dtronix.nexnet.transport.TestServer.readHead;
import static com.dtronix.nexnet.transport.TestServer.write;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.client.TlsOptions;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URI;
import java.util.Arrays;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLSocket;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/** A NexusClient created without a connector connects through {@link NexusConnector#defaults()}. */
@Timeout(30)
class DefaultConnectorClientTest {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, null, 123456789, CLIENT_HASH, new int[] {0});

    static final class Proxy extends ProxyBase {
        Proxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class Nexus extends ClientNexusBase<Proxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    /** The client's byte stream, reassembled from its binary frames. */
    static final class WsBytes {
        private final InputStream in;
        private byte[] pending = new byte[0];

        WsBytes(InputStream in) {
            this.in = in;
        }

        byte[] read(int n) throws Exception {
            while (pending.length < n) {
                TestServer.WsFrame f = readFrame(in);
                assertTrue(f.masked);
                assertEquals(2, f.opcode);
                byte[] joined = Arrays.copyOf(pending, pending.length + f.payload.length);
                System.arraycopy(f.payload, 0, joined, pending.length, f.payload.length);
                pending = joined;
            }
            byte[] head = Arrays.copyOfRange(pending, 0, n);
            pending = Arrays.copyOfRange(pending, n, pending.length);
            return head;
        }

        boolean drained() {
            return pending.length == 0;
        }
    }

    @Test
    void wssClientConnectsHandshakesAndDisconnects() throws Exception {
        CompletableFuture<Integer> disconnectByte = new CompletableFuture<>();
        try (TestServer server = TestServer.tls(false, socket -> {
            ((SSLSocket) socket).startHandshake();
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();
            String response = TestServer.webSocketResponse(readHead(in));
            ByteArrayOutputStream accept = new ByteArrayOutputStream();
            accept.write(FakeServer.PREAMBLE);
            accept.write(FakeServer.serverGreeting(CLIENT_HASH, 42, 4 * 1024 * 1024, 32));
            // Split the NexNet bytes across WebSocket messages: the client must treat them as one stream.
            byte[] nexnet = accept.toByteArray();
            write(out, response);
            out.write(frame(true, 2, Arrays.copyOfRange(nexnet, 0, 5)));
            out.write(frame(true, 2, Arrays.copyOfRange(nexnet, 5, nexnet.length)));
            out.flush();
            // Preamble, then the greeting frame: type, 2-byte length, body.
            WsBytes client = new WsBytes(in);
            byte[] head = client.read(8 + 3);
            assertArrayEquals(FakeServer.PREAMBLE, Arrays.copyOfRange(head, 0, 8));
            int length = (head[9] & 0xFF) | ((head[10] & 0xFF) << 8);
            client.read(length);
            disconnectByte.complete(client.read(1)[0] & 0xFF);
            assertTrue(client.drained());
            TestServer.WsFrame close = readFrame(in);
            assertEquals(8, close.opcode);
        })) {
            ClientConfig config = ClientConfig.webSocket(URI.create("wss://127.0.0.1:" + server.port() + "/nexus"))
                .tls(TlsOptions.builder().trustManager(TcpTlsTransportTest.testCa()).build())
                .build();
            Nexus nexus = new Nexus();
            try (NexusClient<Proxy> client = NexusClient.create(config, HANDSHAKE, nexus, Proxy::new)) {
                client.connect().get(10, TimeUnit.SECONDS);
                assertEquals(ConnectionState.CONNECTED, client.state());
                assertTrue(client.isSecure());
                assertTrue(nexus.context().isSecure());
                assertNotNull(nexus.context().remoteCertificate());
                assertEquals("127.0.0.1", nexus.context().remoteAddress());
                client.disconnect().get(10, TimeUnit.SECONDS);
            }
            assertEquals(DisconnectReason.GRACEFUL.code(), disconnectByte.get(10, TimeUnit.SECONDS));
            server.await();
        }
    }
}
