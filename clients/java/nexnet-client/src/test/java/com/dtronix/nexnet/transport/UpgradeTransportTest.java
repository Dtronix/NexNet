package com.dtronix.nexnet.transport;

import static com.dtronix.nexnet.transport.TestServer.frame;
import static com.dtronix.nexnet.transport.TestServer.readFrame;
import static com.dtronix.nexnet.transport.TestServer.readHead;
import static com.dtronix.nexnet.transport.TestServer.write;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.TlsOptions;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ProtocolException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Base64;
import java.util.Map;
import java.util.Random;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLSocket;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class UpgradeTransportTest {
    private static final String SWITCHING = "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\n"
        + "Upgrade: nexnet-httpsockets\r\n\r\n";

    static Transport http(String uri) throws Exception {
        return http(NexusConnector.defaults(), uri, TlsOptions.defaults());
    }

    static Transport http(NexusConnector connector, String uri, TlsOptions tls) throws Exception {
        return connector.connect(ClientConfig.httpSocket(URI.create(uri)).tls(tls).build(), 10_000);
    }

    static Transport ws(String uri) throws Exception {
        return ws(uri, TlsOptions.defaults());
    }

    static Transport ws(String uri, TlsOptions tls) throws Exception {
        return NexusConnector.defaults().connect(ClientConfig.webSocket(URI.create(uri)).tls(tls).build(), 10_000);
    }

    static byte[] ascii(String s) {
        return s.getBytes(StandardCharsets.US_ASCII);
    }

    static byte[] read(InputStream in, int n) throws Exception {
        byte[] b = new byte[n];
        TestServer.readFully(in, b);
        return b;
    }

    static void echo(InputStream in, OutputStream out) throws Exception {
        byte[] buffer = new byte[1024];
        int n;
        while ((n = in.read(buffer)) > 0) {
            out.write(buffer, 0, n);
            out.flush();
        }
    }

    static NexusTransportException expectFailure(TestServer.Handler handler) throws Exception {
        try (TestServer server = TestServer.plain(handler)) {
            return assertThrows(NexusTransportException.class,
                () -> http("http://127.0.0.1:" + server.port() + "/nexus"));
        }
    }

    // ------------------------------------------------------------------ HttpSocket

    @Test
    void httpSocketSendsTheUpgradeRequestAndSeedsBytesAfterTheHead() throws Exception {
        CompletableFuture<Map<String, String>> request = new CompletableFuture<>();
        try (TestServer server = TestServer.plain(socket -> {
            Map<String, String> head = readHead(socket.getInputStream());
            request.complete(head);
            // The upgrade response and the first session bytes arrive in one write.
            write(socket.getOutputStream(), SWITCHING + "SEED");
            echo(socket.getInputStream(), socket.getOutputStream());
        })) {
            NexusConnector connector = NexusConnector.builder()
                .authorization("Bearer abc")
                .header("X-Custom", "1")
                .queryToken("access_token", "a b&c")
                .build();
            Transport t = http(connector, "http://127.0.0.1:" + server.port() + "/nexus/path?x=1", TlsOptions.defaults());
            try {
                assertFalse(t.isSecure());
                assertEquals("http://", t.name());
                assertNull(t.remoteCertificate());
                assertArrayEquals(ascii("SEED"), read(t.input(), 4));
                t.output().write(ascii("ping"));
                t.output().flush();
                assertArrayEquals(ascii("ping"), read(t.input(), 4));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
            Map<String, String> head = request.get(5, TimeUnit.SECONDS);
            assertEquals("GET /nexus/path?x=1&access_token=a%20b%26c HTTP/1.1", head.get(""));
            assertEquals("127.0.0.1:" + server.port(), head.get("host"));
            assertEquals("Upgrade", head.get("connection"));
            assertEquals("nexnet-httpsockets", head.get("upgrade"));
            assertEquals("Bearer abc", head.get("authorization"));
            assertEquals("1", head.get("x-custom"));
            server.await();
        }
    }

    /** The request head's lines, with the request line first; header names lower-cased. */
    static java.util.List<String[]> captureHead(ClientConfig config, NexusConnector connector) throws Exception {
        CompletableFuture<java.util.List<String[]>> request = new CompletableFuture<>();
        boolean webSocket = config.transport() == com.dtronix.nexnet.client.TransportKind.WEB_SOCKET;
        try (TestServer server = TestServer.plain(socket -> {
            InputStream in = socket.getInputStream();
            ByteArrayOutputStream raw = new ByteArrayOutputStream();
            while (true) {
                int b = in.read();
                raw.write(b);
                byte[] sofar = raw.toByteArray();
                int n = sofar.length;
                if (b < 0 || (n >= 4 && sofar[n - 4] == '\r' && sofar[n - 3] == '\n' && sofar[n - 2] == '\r'
                    && sofar[n - 1] == '\n')) {
                    break;
                }
            }
            String[] lines = new String(raw.toByteArray(), StandardCharsets.ISO_8859_1).split("\r\n");
            java.util.List<String[]> parsed = new java.util.ArrayList<>();
            Map<String, String> head = new java.util.LinkedHashMap<>();
            parsed.add(new String[] {"", lines[0]});
            for (int i = 1; i < lines.length; i++) {
                int colon = lines[i].indexOf(':');
                String name = lines[i].substring(0, colon).trim().toLowerCase(java.util.Locale.ROOT);
                String value = lines[i].substring(colon + 1).trim();
                parsed.add(new String[] {name, value});
                head.put(name, value);
            }
            request.complete(parsed);
            write(socket.getOutputStream(), webSocket ? TestServer.webSocketResponse(head) : SWITCHING);
            echo(socket.getInputStream(), socket.getOutputStream());
        })) {
            URI uri = config.uri();
            URI target = URI.create(uri.getScheme() + "://127.0.0.1:" + server.port() + uri.getRawPath()
                + (uri.getRawQuery() == null ? "" : "?" + uri.getRawQuery()));
            ClientConfig.Builder b = webSocket ? ClientConfig.webSocket(target) : ClientConfig.httpSocket(target);
            for (Map.Entry<String, String> h : config.upgradeHeaders().entrySet()) {
                b.header(h.getKey(), h.getValue());
            }
            if (config.queryTokenName() != null) {
                b.queryToken(config.queryTokenName(), config.queryTokenValue());
            }
            connector.connect(b.build(), 10_000).close(DisconnectReason.GRACEFUL);
            return request.get(5, TimeUnit.SECONDS);
        }
    }

    /** The values of a header in a captured head, in order. */
    static java.util.List<String> values(java.util.List<String[]> head, String name) {
        java.util.List<String> values = new java.util.ArrayList<>();
        for (String[] h : head) {
            if (h[0].equals(name)) {
                values.add(h[1]);
            }
        }
        return values;
    }

    @Test
    void configHeadersAndQueryTokenReachBothUpgradeTransports() throws Exception {
        for (boolean webSocket : new boolean[] {false, true}) {
            URI uri = URI.create((webSocket ? "ws" : "http") + "://placeholder/nexus?x=1");
            ClientConfig.Builder b = webSocket ? ClientConfig.webSocket(uri) : ClientConfig.httpSocket(uri);
            ClientConfig config = b.authorization("Bearer cfg").header("X-Custom", "c")
                .queryToken("access_token", "a b&c").build();
            java.util.List<String[]> head = captureHead(config, NexusConnector.defaults());
            assertEquals("GET /nexus?x=1&access_token=a%20b%26c HTTP/1.1", head.get(0)[1]);
            assertEquals(Arrays.asList("Bearer cfg"), values(head, "authorization"));
            assertEquals(Arrays.asList("c"), values(head, "x-custom"));
        }
    }

    @Test
    void connectorHeadersAndQueryTokenOverrideTheConfig() throws Exception {
        ClientConfig config = ClientConfig.httpSocket(URI.create("http://placeholder/n"))
            .authorization("Bearer cfg").header("X-Config", "1").queryToken("cfg_token", "c").build();
        NexusConnector connector = NexusConnector.builder()
            .header("AUTHORIZATION", "Bearer connector").queryToken("token", "k").build();
        java.util.List<String[]> head = captureHead(config, connector);
        assertEquals("GET /n?token=k HTTP/1.1", head.get(0)[1]);
        assertEquals(Arrays.asList("Bearer connector"), values(head, "authorization"));
        assertEquals(Arrays.asList("1"), values(head, "x-config"));
    }

    @Test
    void httpSocketResponseSplitAcrossManyWrites() throws Exception {
        try (TestServer server = TestServer.plain(socket -> {
            readHead(socket.getInputStream());
            OutputStream out = socket.getOutputStream();
            for (byte b : ascii(SWITCHING + "XY")) {
                out.write(b);
                out.flush();
            }
            echo(socket.getInputStream(), out);
        })) {
            Transport t = http("http://127.0.0.1:" + server.port());
            try {
                assertArrayEquals(ascii("XY"), read(t.input(), 2));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void httpSocketRequestTargetDefaultsToSlash() throws Exception {
        CompletableFuture<String> line = new CompletableFuture<>();
        try (TestServer server = TestServer.plain(socket -> {
            line.complete(readHead(socket.getInputStream()).get(""));
            write(socket.getOutputStream(), SWITCHING);
            echo(socket.getInputStream(), socket.getOutputStream());
        })) {
            http("http://127.0.0.1:" + server.port()).close(DisconnectReason.GRACEFUL);
            assertEquals("GET / HTTP/1.1", line.get(5, TimeUnit.SECONDS));
        }
    }

    @Test
    void hostHeaderOmitsTheDefaultPort() {
        assertEquals("example.com", NexusConnector.hostHeader("example.com", 443, 443));
        assertEquals("example.com:8443", NexusConnector.hostHeader("example.com", 8443, 443));
        assertEquals("[::1]:81", NexusConnector.hostHeader("[::1]", 81, 80));
    }

    @Test
    void unauthorizedAndForbiddenMapToAuthenticationError() throws Exception {
        for (String status : new String[] {"401 Unauthorized", "403 Forbidden"}) {
            NexusTransportException e = expectFailure(socket -> {
                readHead(socket.getInputStream());
                write(socket.getOutputStream(), "HTTP/1.1 " + status + "\r\nContent-Length: 0\r\n\r\n");
            });
            assertEquals(TransportError.AUTHENTICATION_ERROR, e.error(), status);
        }
    }

    @Test
    void otherStatusesMapToProtocolOrInternalError() throws Exception {
        NexusTransportException notFound = expectFailure(socket -> {
            readHead(socket.getInputStream());
            write(socket.getOutputStream(), "HTTP/1.1 404 Not Found\r\n\r\n");
        });
        assertEquals(TransportError.PROTOCOL_ERROR, notFound.error());
        NexusTransportException internal = expectFailure(socket -> {
            readHead(socket.getInputStream());
            write(socket.getOutputStream(), "HTTP/1.1 500 Internal Server Error\r\n\r\n");
        });
        assertEquals(TransportError.INTERNAL_ERROR, internal.error());
    }

    @Test
    void oversizedResponseHeadIsRejected() throws Exception {
        char[] pad = new char[HttpUpgrade.MAX_HEAD_BYTES];
        Arrays.fill(pad, 'a');
        NexusTransportException e = expectFailure(socket -> {
            readHead(socket.getInputStream());
            try {
                write(socket.getOutputStream(), "HTTP/1.1 101 Switching Protocols\r\nX-Pad: " + new String(pad)
                    + "\r\n\r\n");
            } catch (java.io.IOException ignored) {
                // The client may close first.
            }
        });
        assertEquals(TransportError.PROTOCOL_ERROR, e.error());
        assertTrue(e.getMessage().contains("16384"));
    }

    @Test
    void connectionClosedDuringTheHeadIsAborted() throws Exception {
        NexusTransportException e = expectFailure(socket -> {
            readHead(socket.getInputStream());
            write(socket.getOutputStream(), "HTTP/1.1 101 Switching Protocols\r\nUpgr");
        });
        assertEquals(TransportError.CONNECTION_ABORTED, e.error());
    }

    @Test
    void malformedStatusLineIsAProtocolError() throws Exception {
        NexusTransportException e = expectFailure(socket -> {
            readHead(socket.getInputStream());
            write(socket.getOutputStream(), "SSH-2.0-OpenSSH\r\n\r\n");
        });
        assertEquals(TransportError.PROTOCOL_ERROR, e.error());
    }

    @Test
    void httpsSocketRunsOverTls() throws Exception {
        try (TestServer server = TestServer.tls(false, socket -> {
            ((SSLSocket) socket).startHandshake();
            readHead(socket.getInputStream());
            write(socket.getOutputStream(), SWITCHING + "S");
            echo(socket.getInputStream(), socket.getOutputStream());
        })) {
            Transport t = http(NexusConnector.defaults(), "https://127.0.0.1:" + server.port() + "/",
                TlsOptions.builder().trustManager(TcpTlsTransportTest.testCa()).build());
            try {
                assertTrue(t.isSecure());
                assertEquals("https://", t.name());
                assertNotNull(t.remoteCertificate());
                assertArrayEquals(ascii("S"), read(t.input(), 1));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void connectorRejectsUnsafeHeaders() {
        assertThrows(IllegalArgumentException.class, () -> NexusConnector.builder().header("X-A", "a\r\nB: c"));
        assertThrows(IllegalArgumentException.class, () -> NexusConnector.builder().header("Bad Name", "x"));
        assertThrows(IllegalArgumentException.class, () -> NexusConnector.builder().header("Upgrade", "x"));
        assertThrows(IllegalArgumentException.class,
            () -> NexusConnector.builder().header("Sec-WebSocket-Protocol", "x"));
    }

    // ------------------------------------------------------------------ WebSocket

    @Test
    void base64AndAcceptMatchTheRfc() {
        Random random = new Random(1);
        for (int n = 0; n < 40; n++) {
            byte[] data = new byte[n];
            random.nextBytes(data);
            assertEquals(Base64.getEncoder().encodeToString(data), Base64Encoder.encode(data));
        }
        // RFC 6455 section 1.3.
        assertEquals("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", NexusConnector.acceptFor("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    @Test
    void webSocketHandshakeAndMaskedBinaryFrames() throws Exception {
        CompletableFuture<Map<String, String>> request = new CompletableFuture<>();
        ByteArrayOutputStream received = new ByteArrayOutputStream();
        try (TestServer server = TestServer.plain(socket -> {
            InputStream in = socket.getInputStream();
            Map<String, String> head = readHead(in);
            request.complete(head);
            // The response and a first frame arrive in one write.
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            out.write(TestServer.webSocketResponse(head).getBytes(StandardCharsets.ISO_8859_1));
            out.write(frame(true, 2, ascii("AB")));
            socket.getOutputStream().write(out.toByteArray());
            socket.getOutputStream().flush();
            int total = 0;
            while (total < 5 + 40_000) {
                TestServer.WsFrame f = readFrame(in);
                assertTrue(f.masked, "client frames are masked");
                assertEquals(2, f.opcode);
                assertTrue(f.fin);
                assertTrue(f.payload.length <= WebSocketStreams.MAX_OUTGOING_PAYLOAD);
                received.write(f.payload);
                total += f.payload.length;
            }
            TestServer.WsFrame close = readFrame(in);
            assertEquals(8, close.opcode);
            assertEquals(1000, ((close.payload[0] & 0xFF) << 8) | (close.payload[1] & 0xFF));
        })) {
            Transport t = ws("ws://127.0.0.1:" + server.port() + "/ws?k=v");
            byte[] big = new byte[40_000];
            new Random(2).nextBytes(big);
            try {
                assertFalse(t.isSecure());
                assertEquals("ws://", t.name());
                assertArrayEquals(ascii("AB"), read(t.input(), 2));
                t.output().write(ascii("hello"));
                t.output().flush();
                t.output().write(big);
                t.output().flush();
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
            server.await();
            Map<String, String> head = request.get();
            assertEquals("GET /ws?k=v HTTP/1.1", head.get(""));
            assertEquals("websocket", head.get("upgrade"));
            assertEquals("Upgrade", head.get("connection"));
            assertEquals("13", head.get("sec-websocket-version"));
            assertEquals(16, Base64.getDecoder().decode(head.get("sec-websocket-key")).length);
            assertFalse(head.containsKey("sec-websocket-protocol"));
            assertFalse(head.containsKey("sec-websocket-extensions"));
            byte[] all = received.toByteArray();
            assertArrayEquals(ascii("hello"), Arrays.copyOfRange(all, 0, 5));
            assertArrayEquals(big, Arrays.copyOfRange(all, 5, all.length));
        }
    }

    @Test
    void fragmentedMessagesPingsAndLongLengthsFormOneByteStream() throws Exception {
        byte[] medium = new byte[300];
        byte[] large = new byte[70_000];
        new Random(3).nextBytes(medium);
        new Random(4).nextBytes(large);
        CompletableFuture<byte[]> pong = new CompletableFuture<>();
        try (TestServer server = TestServer.plain(socket -> {
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();
            write(out, TestServer.webSocketResponse(readHead(in)));
            out.write(frame(false, 2, ascii("he")));
            out.write(frame(true, 9, ascii("p1")));
            out.write(frame(false, 0, ascii("l")));
            out.write(frame(false, 0, new byte[0]));
            out.write(frame(true, 0, ascii("lo")));
            out.write(frame(true, 2, new byte[0]));
            out.write(frame(true, 10, ascii("unsolicited pong")));
            out.write(frame(true, 2, medium));
            out.write(frame(true, 2, large));
            out.flush();
            TestServer.WsFrame f = readFrame(in);
            assertEquals(10, f.opcode);
            assertTrue(f.masked);
            pong.complete(f.payload);
            // Close: the client answers with the same code and its input ends.
            out.write(frame(true, 8, new byte[] {0x03, (byte) 0xE9}));
            out.flush();
            TestServer.WsFrame close = readFrame(in);
            assertEquals(8, close.opcode);
            assertArrayEquals(new byte[] {0x03, (byte) 0xE9}, close.payload);
        })) {
            Transport t = ws("ws://127.0.0.1:" + server.port());
            try {
                assertArrayEquals(ascii("hello"), read(t.input(), 5));
                assertArrayEquals(medium, read(t.input(), medium.length));
                assertArrayEquals(large, read(t.input(), large.length));
                assertArrayEquals(ascii("p1"), pong.get(5, TimeUnit.SECONDS));
                assertEquals(-1, t.input().read());
                assertEquals(-1, t.input().read());
                server.await();
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    static void expectReadFailure(byte[] badFrame, int expectedCloseCode) throws Exception {
        CompletableFuture<Integer> closeCode = new CompletableFuture<>();
        try (TestServer server = TestServer.plain(socket -> {
            InputStream in = socket.getInputStream();
            OutputStream out = socket.getOutputStream();
            write(out, TestServer.webSocketResponse(readHead(in)));
            out.write(badFrame);
            out.flush();
            TestServer.WsFrame close = readFrame(in);
            closeCode.complete(close.opcode == 8
                ? ((close.payload[0] & 0xFF) << 8) | (close.payload[1] & 0xFF) : -close.opcode);
        })) {
            Transport t = ws("ws://127.0.0.1:" + server.port());
            try {
                assertThrows(ProtocolException.class, () -> t.input().read());
                assertEquals(expectedCloseCode, closeCode.get(5, TimeUnit.SECONDS));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
        }
    }

    @Test
    void textMessagesAreRejected() throws Exception {
        expectReadFailure(frame(true, 1, ascii("text")), 1003);
    }

    @Test
    void maskedServerFramesAreRejected() throws Exception {
        expectReadFailure(frame(true, 2, ascii("x"), new byte[] {1, 2, 3, 4}), 1002);
    }

    @Test
    void reservedBitsAreRejected() throws Exception {
        byte[] f = frame(true, 2, ascii("x"));
        f[0] |= 0x40;
        expectReadFailure(f, 1002);
    }

    @Test
    void continuationWithoutAMessageIsRejected() throws Exception {
        expectReadFailure(frame(true, 0, ascii("x")), 1002);
    }

    @Test
    void fragmentedControlFramesAreRejected() throws Exception {
        expectReadFailure(frame(false, 9, ascii("x")), 1002);
    }

    static NexusTransportException expectWsFailure(String response) throws Exception {
        try (TestServer server = TestServer.plain(socket -> {
            Map<String, String> head = readHead(socket.getInputStream());
            write(socket.getOutputStream(), response.replace("{accept}", TestServer.accept(
                head.get("sec-websocket-key"))));
        })) {
            return assertThrows(NexusTransportException.class, () -> ws("ws://127.0.0.1:" + server.port()));
        }
    }

    @Test
    void wrongAcceptIsRejected() throws Exception {
        NexusTransportException e = expectWsFailure("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
            + "Connection: Upgrade\r\nSec-WebSocket-Accept: AAAAAAAAAAAAAAAAAAAAAAAAAAA=\r\n\r\n");
        assertEquals(TransportError.PROTOCOL_ERROR, e.error());
    }

    @Test
    void unrequestedSubprotocolOrExtensionIsRejected() throws Exception {
        String base = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: {accept}\r\n";
        assertEquals(TransportError.PROTOCOL_ERROR,
            expectWsFailure(base + "Sec-WebSocket-Protocol: chat\r\n\r\n").error());
        assertEquals(TransportError.PROTOCOL_ERROR,
            expectWsFailure(base + "Sec-WebSocket-Extensions: permessage-deflate\r\n\r\n").error());
        assertEquals(TransportError.PROTOCOL_ERROR, expectWsFailure("HTTP/1.1 101 Switching Protocols\r\n"
            + "Connection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n").error());
    }

    @Test
    void webSocketRejectionStatusMapsToAuthenticationError() throws Exception {
        assertEquals(TransportError.AUTHENTICATION_ERROR,
            expectWsFailure("HTTP/1.1 401 Unauthorized\r\n\r\n").error());
    }

    @Test
    void wssRunsOverTls() throws Exception {
        try (TestServer server = TestServer.tls(false, socket -> {
            ((SSLSocket) socket).startHandshake();
            InputStream in = socket.getInputStream();
            write(socket.getOutputStream(), TestServer.webSocketResponse(readHead(in)));
            TestServer.WsFrame f = readFrame(in);
            socket.getOutputStream().write(frame(true, 2, f.payload));
            socket.getOutputStream().flush();
            readFrame(in);
        })) {
            Transport t = ws("wss://127.0.0.1:" + server.port() + "/",
                TlsOptions.builder().trustManager(TcpTlsTransportTest.testCa()).build());
            try {
                assertTrue(t.isSecure());
                assertEquals("wss://", t.name());
                assertNotNull(t.remoteCertificate());
                t.output().write(ascii("tls"));
                t.output().flush();
                assertArrayEquals(ascii("tls"), read(t.input(), 3));
            } finally {
                t.close(DisconnectReason.GRACEFUL);
            }
            server.await();
        }
    }

    @Test
    void wssHostnameMismatchIsRejected() throws Exception {
        try (TestServer server = TestServer.tls(false, TcpTlsTransportTest::handshakeOnly)) {
            Exception e = assertThrows(Exception.class, () -> ws("wss://127.0.0.1:" + server.port() + "/",
                TlsOptions.builder().trustManager(TcpTlsTransportTest.testCa()).serverName("example.com").build()));
            TcpTlsTransportTest.assertAuthenticationFailure(e);
        }
    }
}
