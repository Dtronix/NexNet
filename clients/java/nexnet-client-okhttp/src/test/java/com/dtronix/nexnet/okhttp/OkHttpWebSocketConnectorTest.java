package com.dtronix.nexnet.okhttp;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import com.dtronix.nexnet.spi.ProxyBase;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.Transport;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.SocketTimeoutException;
import java.net.URI;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import mockwebserver3.MockResponse;
import mockwebserver3.MockWebServer;
import mockwebserver3.RecordedRequest;
import okhttp3.OkHttpClient;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.Buffer;
import okio.ByteString;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

class OkHttpWebSocketConnectorTest {
    private MockWebServer server;
    private final OkHttpWebSocketConnector connector = OkHttpWebSocketConnector.create();

    @BeforeEach
    void start() throws IOException {
        server = new MockWebServer();
        server.start(0);
    }

    @AfterEach
    void stop() {
        server.close();
    }

    private URI uri(String path) {
        return URI.create("ws://" + server.getHostName() + ":" + server.getPort() + path);
    }

    /** A server side that echoes binary messages and records what it received. */
    static final class EchoServer extends WebSocketListener {
        final CompletableFuture<WebSocket> opened = new CompletableFuture<>();
        final LinkedBlockingQueue<ByteString> messages = new LinkedBlockingQueue<>();
        final CompletableFuture<Integer> closeCode = new CompletableFuture<>();

        @Override
        public void onOpen(WebSocket webSocket, Response response) {
            opened.complete(webSocket);
        }

        @Override
        public void onMessage(WebSocket webSocket, ByteString bytes) {
            messages.add(bytes);
            webSocket.send(bytes);
        }

        @Override
        public void onClosing(WebSocket webSocket, int code, String reason) {
            closeCode.complete(code);
            webSocket.close(1000, null);
        }
    }

    private static byte[] readFully(InputStream in, int length) throws IOException {
        byte[] data = new byte[length];
        int read = 0;
        while (read < length) {
            int n = in.read(data, read, length - read);
            if (n < 0) {
                throw new IOException("End of stream after " + read + " bytes.");
            }
            read += n;
        }
        return data;
    }

    @Test
    void bytesFlowBothWaysAndFramesSpanMessages() throws Exception {
        EchoServer echo = new EchoServer();
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(echo).build());
        Transport transport = connector.connect(ClientConfig.webSocket(uri("/nexus")).build(), 5_000);
        try {
            assertFalse(transport.isSecure());
            assertNull(transport.remoteCertificate());
            assertEquals("ws://", transport.name());

            transport.output().write(new byte[] {1, 2, 3});
            transport.output().flush();
            transport.output().write(new byte[] {4, 5});
            transport.output().flush();
            assertArrayEquals(new byte[] {1, 2, 3, 4, 5}, readFully(transport.input(), 5));

            // A write larger than one message is split into several and arrives whole.
            byte[] large = new byte[OkHttpWebSocketTransport.MAX_MESSAGE * 2 + 17];
            for (int i = 0; i < large.length; i++) {
                large[i] = (byte) (i * 31);
            }
            transport.output().write(large);
            transport.output().flush();
            assertArrayEquals(large, readFully(transport.input(), large.length));
            assertEquals(OkHttpWebSocketTransport.MAX_MESSAGE, echo.messages.toArray(new ByteString[0])[2].size());
        } finally {
            transport.close(DisconnectReason.GRACEFUL);
        }
        assertEquals(1000, echo.closeCode.get(5, TimeUnit.SECONDS));
    }

    @Test
    void upgradeCarriesHeadersAndQueryToken() throws Exception {
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(new EchoServer()).build());
        ClientConfig config = ClientConfig.webSocket(uri("/nexus?a=1"))
            .authorization("Bearer abc")
            .header("X-Test", "value")
            .queryToken("access_token", "t o&k")
            .build();
        Transport transport = connector.connect(config, 5_000);
        transport.close(DisconnectReason.GRACEFUL);

        RecordedRequest request = server.takeRequest(5, TimeUnit.SECONDS);
        assertNotNull(request);
        assertEquals("Bearer abc", request.getHeaders().get("Authorization"));
        assertEquals("value", request.getHeaders().get("X-Test"));
        assertEquals("websocket", request.getHeaders().get("Upgrade"));
        assertNull(request.getHeaders().get("Sec-WebSocket-Protocol"));
        assertEquals("/nexus", request.getUrl().encodedPath());
        assertEquals("1", request.getUrl().queryParameter("a"));
        assertEquals("t o&k", request.getUrl().queryParameter("access_token"));
    }

    @Test
    void refusedUpgradesMapToTransportErrors() {
        server.enqueue(new MockResponse.Builder().code(401).build());
        server.enqueue(new MockResponse.Builder().code(302).addHeader("Location", "/elsewhere").build());
        server.enqueue(new MockResponse.Builder().code(500).build());
        server.enqueue(new MockResponse.Builder().code(404).build());

        ClientConfig config = ClientConfig.webSocket(uri("/nexus")).build();
        assertEquals(TransportError.AUTHENTICATION_ERROR,
            assertThrows(NexusTransportException.class, () -> connector.connect(config, 5_000)).error());
        NexusTransportException redirect =
            assertThrows(NexusTransportException.class, () -> connector.connect(config, 5_000));
        assertEquals(TransportError.PROTOCOL_ERROR, redirect.error());
        assertTrue(redirect.getMessage().contains("302"), redirect.getMessage());
        assertEquals(TransportError.INTERNAL_ERROR,
            assertThrows(NexusTransportException.class, () -> connector.connect(config, 5_000)).error());
        assertEquals(TransportError.PROTOCOL_ERROR,
            assertThrows(NexusTransportException.class, () -> connector.connect(config, 5_000)).error());
        assertEquals(4, server.getRequestCount(), "The redirect was not followed.");
    }

    @Test
    void connectTimesOut() {
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(new EchoServer())
            .headersDelay(10, TimeUnit.SECONDS).build());
        ClientConfig config = ClientConfig.webSocket(uri("/nexus")).build();
        assertThrows(SocketTimeoutException.class, () -> connector.connect(config, 200));
    }

    @Test
    void onlyWebSocketConfigurations() {
        NexusTransportException e = assertThrows(NexusTransportException.class,
            () -> connector.connect(ClientConfig.tcp("localhost", 1).build(), 1_000));
        assertEquals(TransportError.INVALID_ADDRESS, e.error());
    }

    @Test
    void serverCloseEndsTheInput() throws Exception {
        EchoServer echo = new EchoServer();
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(echo).build());
        Transport transport = connector.connect(ClientConfig.webSocket(uri("/nexus")).build(), 5_000);
        try {
            echo.opened.get(5, TimeUnit.SECONDS).close(1000, "bye");
            assertEquals(-1, transport.input().read());
            assertEquals(-1, transport.input().read());
        } finally {
            transport.close(DisconnectReason.GRACEFUL);
        }
    }

    @Test
    void textMessageFailsTheInput() throws Exception {
        EchoServer echo = new EchoServer();
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(echo).build());
        Transport transport = connector.connect(ClientConfig.webSocket(uri("/nexus")).build(), 5_000);
        try {
            echo.opened.get(5, TimeUnit.SECONDS).send("text");
            IOException e = assertThrows(IOException.class, () -> transport.input().read());
            assertTrue(e.getMessage().contains("text"), e.getMessage());
            assertEquals(1003, echo.closeCode.get(5, TimeUnit.SECONDS));
        } finally {
            transport.close(DisconnectReason.GRACEFUL);
        }
    }

    @Test
    void closeUnblocksAPendingRead() throws Exception {
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(new EchoServer()).build());
        Transport transport = connector.connect(ClientConfig.webSocket(uri("/nexus")).build(), 5_000);
        CountDownLatch reading = new CountDownLatch(1);
        CompletableFuture<Integer> read = CompletableFuture.supplyAsync(() -> {
            reading.countDown();
            try {
                return transport.input().read();
            } catch (IOException e) {
                return -2;
            }
        });
        assertTrue(reading.await(5, TimeUnit.SECONDS));
        Thread.sleep(50);
        transport.close(DisconnectReason.GRACEFUL);
        transport.close(DisconnectReason.GRACEFUL);
        assertEquals(-1, read.get(5, TimeUnit.SECONDS));
        assertThrows(IOException.class, () -> {
            transport.output().write(1);
            transport.output().flush();
        });
    }

    /** A server that answers the NexNet handshake with the golden ServerGreeting. */
    static final class GreetingServer extends WebSocketListener {
        /** The NexNet preamble. */
        static final byte[] PREAMBLE = {0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1};
        /** golden-frames.txt (nexnet-client tests) "server-greeting": version -987654321, clientId 42, 4 MiB, 32. */
        static final String SERVER_GREETING = "690d0094d2c521974f2ace0040000020";

        private final ByteArrayOutputStream received = new ByteArrayOutputStream();
        private boolean greeted;

        @Override
        public synchronized void onMessage(WebSocket webSocket, ByteString bytes) {
            received.write(bytes.toByteArray(), 0, bytes.size());
            byte[] all = received.toByteArray();
            // Preamble (8 bytes), then the ClientGreeting frame: type, little-endian length, body.
            if (!greeted && all.length >= 11 && all.length >= 11 + ((all[9] & 0xFF) | (all[10] & 0xFF) << 8)) {
                greeted = true;
                webSocket.send(new Buffer().write(PREAMBLE).write(ByteString.decodeHex(SERVER_GREETING))
                    .readByteString());
            }
        }

        @Override
        public void onClosing(WebSocket webSocket, int code, String reason) {
            webSocket.close(1000, null);
        }
    }

    static final class TestProxy extends ProxyBase {
        TestProxy(ProxyInvoker invoker) {
            super(invoker);
        }
    }

    static final class TestNexus extends ClientNexusBase<TestProxy> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments,
                                                  InvocationContext context) {
            throw new IllegalArgumentException("Unknown method id " + methodId);
        }
    }

    @Test
    void aNexusClientConnectsOverOkHttp() throws Exception {
        server.enqueue(new MockResponse.Builder().webSocketUpgrade(new GreetingServer()).build());
        HandshakeDescriptor handshake = new HandshakeDescriptor(
            NexusRuntime.VERSION, "v2", 123456789, -987654321, new int[] {0});
        try (NexusClient<TestProxy> client = NexusClient.create(
            ClientConfig.webSocket(uri("/nexus")).build(), handshake, new TestNexus(), TestProxy::new, connector)) {
            client.connect().get(10, TimeUnit.SECONDS);
            assertEquals(ConnectionState.CONNECTED, client.state());
            assertFalse(client.isSecure());
            client.disconnect().get(10, TimeUnit.SECONDS);
            assertEquals(ConnectionState.DISCONNECTED, client.state());
        }
    }

    @Test
    void theGivenClientIsNotChanged() {
        OkHttpClient original = new OkHttpClient();
        OkHttpWebSocketConnector own = new OkHttpWebSocketConnector(original);
        assertTrue(original.followRedirects());
        assertFalse(own.client().followRedirects());
        assertFalse(own.client().followSslRedirects());
        assertEquals(0, own.client().readTimeoutMillis());
    }
}
