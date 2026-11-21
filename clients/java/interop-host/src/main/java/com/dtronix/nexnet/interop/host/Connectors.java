package com.dtronix.nexnet.interop.host;

import com.dtronix.nexnet.client.TlsOptions;
import com.dtronix.nexnet.okhttp.OkHttpWebSocketConnector;
import com.dtronix.nexnet.transport.NexusConnector;
import com.dtronix.nexnet.transport.TlsTrust;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import javax.net.ssl.KeyManager;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509KeyManager;
import javax.net.ssl.X509TrustManager;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

/**
 * The connector the host's clients use. By default every transport uses the built-in {@link NexusConnector}. With
 * {@code NEXNET_JAVA_WEBSOCKET=okhttp} in the host's environment (Q08), the WebSocket transports use the OkHttp connector
 * of {@code nexnet-client-okhttp} instead, with the spec's TLS options mapped onto the {@link OkHttpClient} (the OkHttp
 * connector does not read {@code ClientConfig.tls()}), and the host reports only WebSocket and WebSocketTls in its hello,
 * so the runner skips the rows of the other transports.
 */
final class Connectors {
    /** The environment variable that selects the WebSocket connector: {@code okhttp}, or unset for the built-in one. */
    static final String ENVIRONMENT_VARIABLE = "NEXNET_JAVA_WEBSOCKET";

    /** The transports of the OkHttp mode. */
    static final List<String> OKHTTP_TRANSPORTS = Arrays.asList("WebSocket", "WebSocketTls");

    private static final boolean OKHTTP = parse(System.getenv(ENVIRONMENT_VARIABLE));

    private Connectors() {
    }

    /**
     * Reads a {@code NEXNET_JAVA_WEBSOCKET} value.
     *
     * @param value the value, or null when unset
     * @return true for the OkHttp connector
     * @throws IllegalArgumentException for an unknown value
     */
    static boolean parse(String value) {
        if (value == null || value.trim().isEmpty() || value.trim().equalsIgnoreCase("builtin")) {
            return false;
        }
        if (value.trim().toLowerCase(Locale.ROOT).equals("okhttp")) {
            return true;
        }
        throw new IllegalArgumentException(ENVIRONMENT_VARIABLE + " must be okhttp, builtin or unset, not '" + value + "'.");
    }

    /** Whether this host runs its WebSocket clients on OkHttp. */
    static boolean okHttp() {
        return OKHTTP;
    }

    /**
     * The connector of a spec.
     *
     * @param transport the spec's transport name
     * @param tls the TLS options of a secure transport, or null
     * @return the connector
     */
    static Transport.Connector select(String transport, TlsOptions tls) {
        return select(transport, tls, OKHTTP);
    }

    static Transport.Connector select(String transport, TlsOptions tls, boolean okHttp) {
        if (!okHttp) {
            return NexusConnector.defaults();
        }
        switch (transport) {
            case "WebSocket":
                return Shared.PLAIN;
            case "WebSocketTls":
                return new OkHttpWebSocketConnector(secure(tls == null ? TlsOptions.defaults() : tls));
            default:
                throw HostError.notSupported("The JVM host runs only WebSocket transports on OkHttp ("
                    + ENVIRONMENT_VARIABLE + "=okhttp), not " + transport + ".");
        }
    }

    /** The OkHttp client of a secure connection: the shared one with the spec's trust, client certificate and pins. */
    static OkHttpClient secure(TlsOptions options) {
        try {
            X509TrustManager configured = options.trustManager();
            X509TrustManager trust = configured != null ? configured : TlsTrust.systemTrustManager();
            List<byte[]> pins = options.pinnedCertificateSha256();
            X509TrustManager effective = pins.isEmpty() ? trust : new Pinning(trust, pins);
            X509KeyManager key = options.keyManager();
            SSLContext context = SSLContext.getInstance("TLS");
            context.init(key == null ? null : new KeyManager[] {key}, new TrustManager[] {effective}, null);
            OkHttpClient.Builder builder = Shared.BASE.newBuilder()
                .sslSocketFactory(context.getSocketFactory(), effective);
            if (!options.hostnameVerification()) {
                builder.hostnameVerifier((host, session) -> true);
            }
            return builder.build();
        } catch (GeneralSecurityException e) {
            throw HostError.badRequest("Bad TLS options: " + e.getMessage());
        }
    }

    /**
     * In the OkHttp mode, loads and initializes OkHttp and TLS before the host says hello: an upgrade request to a
     * loopback socket that answers 404, and a TLS socket of the system trust. A cold OkHttp and TLS stack otherwise
     * takes seconds to make the first connection, longer than the tests give a connect.
     */
    static void warmUp() {
        if (!OKHTTP) {
            return;
        }
        try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            Thread responder = new Thread(() -> {
                try (Socket socket = server.accept()) {
                    socket.getOutputStream().write(
                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                            .getBytes(StandardCharsets.US_ASCII));
                    socket.getOutputStream().flush();
                } catch (IOException ignored) {
                    // The warm-up only loads classes.
                }
            }, "nexnet-host-okhttp-warmup");
            responder.setDaemon(true);
            responder.start();
            CountDownLatch done = new CountDownLatch(1);
            Request request = new Request.Builder()
                .url("http://" + server.getInetAddress().getHostAddress() + ":" + server.getLocalPort() + "/")
                .build();
            WebSocket ws = Shared.BASE.newWebSocket(request, new WebSocketListener() {
                @Override
                public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                    done.countDown();
                }

                @Override
                public void onOpen(WebSocket webSocket, Response response) {
                    webSocket.cancel();
                    done.countDown();
                }
            });
            if (!done.await(10, TimeUnit.SECONDS)) {
                ws.cancel();
            }
            secure(TlsOptions.defaults()).sslSocketFactory().createSocket().close();
        } catch (IOException | RuntimeException e) {
            System.err.println("The OkHttp warm-up failed (the first connections may be slow): " + e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    /** The shared OkHttp client (one dispatcher and connection pool for the host), created on first use. */
    private static final class Shared {
        static final OkHttpClient BASE = new OkHttpClient();
        static final OkHttpWebSocketConnector PLAIN = new OkHttpWebSocketConnector(BASE);
    }

    /** Accepts a chain the delegate trusts and some pin matches (DER or SPKI SHA-256), as the built-in transport. */
    static final class Pinning implements X509TrustManager {
        private final X509TrustManager delegate;
        private final List<byte[]> pins;

        Pinning(X509TrustManager delegate, List<byte[]> pins) {
            this.delegate = delegate;
            this.pins = new ArrayList<>(pins);
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            delegate.checkClientTrusted(chain, authType);
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            delegate.checkServerTrusted(chain, authType);
            for (X509Certificate certificate : chain) {
                if (matches(TlsTrust.spkiSha256(certificate)) || matches(TlsTrust.certificateSha256(certificate))) {
                    return;
                }
            }
            throw new CertificateException("The server certificate chain matches no pinned certificate.");
        }

        private boolean matches(byte[] hash) {
            for (byte[] pin : pins) {
                if (MessageDigest.isEqual(hash, pin)) {
                    return true;
                }
            }
            return false;
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return delegate.getAcceptedIssuers();
        }
    }
}
