package com.dtronix.nexnet.transport;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.KeyStore;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.TrustManagerFactory;

/** A one-connection socket server for transport tests, plain or TLS (the F08 test certificates). */
final class TestServer implements AutoCloseable {
    interface Handler {
        void handle(Socket socket) throws Exception;
    }

    static final char[] PFX_PASSWORD = "certPass".toCharArray();

    private final ServerSocket server;
    private final Thread thread;
    /** Completes when the handler returns, or with its failure. */
    final CompletableFuture<Void> done = new CompletableFuture<>();

    private TestServer(ServerSocket server, Handler handler) {
        this.server = server;
        this.thread = new Thread(() -> {
            try (Socket socket = server.accept()) {
                socket.setSoTimeout(10_000);
                handler.handle(socket);
                done.complete(null);
            } catch (Throwable e) {
                done.completeExceptionally(e);
            }
        }, "test-server");
        thread.setDaemon(true);
        thread.start();
    }

    static TestServer plain(Handler handler) throws IOException {
        return new TestServer(new ServerSocket(0, 1, InetAddress.getLoopbackAddress()), handler);
    }

    static TestServer tls(boolean needClientAuth, Handler handler) throws Exception {
        SSLServerSocket socket = (SSLServerSocket) serverContext().getServerSocketFactory()
            .createServerSocket(0, 1, InetAddress.getLoopbackAddress());
        socket.setNeedClientAuth(needClientAuth);
        return new TestServer(socket, handler);
    }

    int port() {
        return server.getLocalPort();
    }

    /** Waits for the handler; rethrows its failure. */
    void await() throws Exception {
        try {
            done.get(15, TimeUnit.SECONDS);
        } catch (java.util.concurrent.ExecutionException e) {
            if (e.getCause() instanceof Exception) {
                throw (Exception) e.getCause();
            }
            throw e;
        }
    }

    @Override
    public void close() throws IOException {
        server.close();
    }

    // ------------------------------------------------------------------ certificates

    static Path certs() {
        Path dir = Paths.get("").toAbsolutePath();
        while (dir != null) {
            Path candidate = dir.resolve("src/NexNet.IntegrationTests/certs");
            if (Files.isDirectory(candidate)) {
                return candidate;
            }
            dir = dir.getParent();
        }
        throw new IllegalStateException("src/NexNet.IntegrationTests/certs not found above the working directory.");
    }

    static InputStream cert(String name) throws IOException {
        return new FileInputStream(certs().resolve(name).toFile());
    }

    static SSLContext serverContext() throws Exception {
        KeyStore keys = KeyStore.getInstance("PKCS12");
        try (InputStream in = cert("server.pfx")) {
            keys.load(in, PFX_PASSWORD);
        }
        KeyManagerFactory kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        kmf.init(keys, PFX_PASSWORD);
        TrustManagerFactory tmf = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        try (InputStream in = cert("test-ca.pem")) {
            tmf.init(TlsTrustTestSupport.storeOf(in));
        }
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(kmf.getKeyManagers(), tmf.getTrustManagers(), null);
        return context;
    }

    // ------------------------------------------------------------------ HTTP

    /** Reads a request head; returns the request line under "" and lower-cased header names. */
    static Map<String, String> readHead(InputStream in) throws IOException {
        ByteArrayOutputStream head = new ByteArrayOutputStream();
        int matched = 0;
        while (matched < 4) {
            int b = in.read();
            if (b < 0) {
                throw new EOFException("EOF inside the request head");
            }
            head.write(b);
            matched = (b == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (b == '\r' ? 1 : 0);
        }
        String[] lines = new String(head.toByteArray(), StandardCharsets.ISO_8859_1).split("\r\n");
        Map<String, String> map = new LinkedHashMap<>();
        map.put("", lines[0]);
        for (int i = 1; i < lines.length; i++) {
            int colon = lines[i].indexOf(':');
            map.put(lines[i].substring(0, colon).trim().toLowerCase(Locale.ROOT), lines[i].substring(colon + 1).trim());
        }
        return map;
    }

    static void write(OutputStream out, String text) throws IOException {
        out.write(text.getBytes(StandardCharsets.ISO_8859_1));
        out.flush();
    }

    static String accept(String key) throws Exception {
        java.security.MessageDigest sha1 = java.security.MessageDigest.getInstance("SHA-1");
        return Base64.getEncoder().encodeToString(
            sha1.digest((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").getBytes(StandardCharsets.ISO_8859_1)));
    }

    static String webSocketResponse(Map<String, String> request) throws Exception {
        return "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: " + accept(request.get("sec-websocket-key")) + "\r\n\r\n";
    }

    // ------------------------------------------------------------------ WebSocket frames (server side)

    /** A decoded frame. */
    static final class WsFrame {
        final boolean fin;
        final int opcode;
        final boolean masked;
        final byte[] payload;

        WsFrame(boolean fin, int opcode, boolean masked, byte[] payload) {
            this.fin = fin;
            this.opcode = opcode;
            this.masked = masked;
            this.payload = payload;
        }
    }

    static byte[] frame(boolean fin, int opcode, byte[] payload) {
        return frame(fin, opcode, payload, null);
    }

    /** An unmasked server frame, or a masked one when mask is given. */
    static byte[] frame(boolean fin, int opcode, byte[] payload, byte[] mask) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        out.write((fin ? 0x80 : 0) | opcode);
        int maskBit = mask == null ? 0 : 0x80;
        int len = payload.length;
        if (len < 126) {
            out.write(maskBit | len);
        } else if (len < 65536) {
            out.write(maskBit | 126);
            out.write(len >>> 8);
            out.write(len & 0xFF);
        } else {
            out.write(maskBit | 127);
            for (int i = 7; i >= 0; i--) {
                out.write((int) (((long) len >>> (8 * i)) & 0xFF));
            }
        }
        if (mask != null) {
            out.write(mask, 0, 4);
            for (int i = 0; i < len; i++) {
                out.write(payload[i] ^ mask[i & 3]);
            }
        } else {
            out.write(payload, 0, len);
        }
        return out.toByteArray();
    }

    static WsFrame readFrame(InputStream in) throws IOException {
        int b0 = readByte(in);
        int b1 = readByte(in);
        long len = b1 & 0x7F;
        if (len == 126) {
            len = (readByte(in) << 8) | readByte(in);
        } else if (len == 127) {
            len = 0;
            for (int i = 0; i < 8; i++) {
                len = (len << 8) | readByte(in);
            }
        }
        boolean masked = (b1 & 0x80) != 0;
        byte[] mask = new byte[4];
        if (masked) {
            readFully(in, mask);
        }
        byte[] payload = new byte[(int) len];
        readFully(in, payload);
        if (masked) {
            for (int i = 0; i < payload.length; i++) {
                payload[i] ^= mask[i & 3];
            }
        }
        return new WsFrame((b0 & 0x80) != 0, b0 & 0x0F, masked, payload);
    }

    static int readByte(InputStream in) throws IOException {
        int b = in.read();
        if (b < 0) {
            throw new EOFException();
        }
        return b;
    }

    static void readFully(InputStream in, byte[] b) throws IOException {
        int off = 0;
        while (off < b.length) {
            int n = in.read(b, off, b.length - off);
            if (n < 0) {
                throw new EOFException();
            }
            off += n;
        }
    }
}
