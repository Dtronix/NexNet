package com.dtronix.nexnet.transport;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.client.ClientConfig;
import com.dtronix.nexnet.client.TlsOptions;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.UnsupportedEncodingException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.URI;
import java.net.URLEncoder;
import java.nio.charset.Charset;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLSocket;
import org.jspecify.annotations.Nullable;

/**
 * The built-in transports (D4): TCP, TCP with TLS, HttpSocket ({@code http://}, {@code https://}) and WebSocket
 * ({@code ws://}, {@code wss://}, RFC 6455, binary messages, no subprotocol). Unix domain sockets are not available.
 * {@link #defaults()} is the connector a client uses unless it is given another.
 *
 * <p>The upgrade transports can carry extra request headers and a query-string token, for servers that authorize
 * the HTTP request itself (NexNet.Asp):
 *
 * <pre>{@code
 * Transport.Connector connector = NexusConnector.builder()
 *     .authorization("Bearer " + jwt)
 *     .build();
 * }</pre>
 *
 * <p>Every connection sets {@code TCP_NODELAY}. TLS verifies the host name unless the options turn it off, sends
 * SNI for host names, and negotiates no ALPN. Socket connect, TLS handshake and upgrade together are bounded by the
 * connection timeout.
 */
public final class NexusConnector implements Transport.Connector {
    private static final NexusConnector DEFAULTS = new NexusConnector(Collections.<String[]>emptyList(), null, null);
    private static final String WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static final Charset LATIN1 = Charset.forName("ISO-8859-1");
    private static final SecureRandom RANDOM = new SecureRandom();

    private final List<String[]> headers;
    private final @Nullable String queryName;
    private final @Nullable String queryValue;

    private NexusConnector(List<String[]> headers, @Nullable String queryName, @Nullable String queryValue) {
        this.headers = headers;
        this.queryName = queryName;
        this.queryValue = queryValue;
    }

    /**
     * The connector without extra headers.
     *
     * @return the shared default connector
     */
    public static NexusConnector defaults() {
        return DEFAULTS;
    }

    /**
     * Starts building a connector with extra upgrade request headers or a query-string token.
     *
     * @return a builder
     */
    public static Builder builder() {
        return new Builder();
    }

    @Override
    public Transport connect(ClientConfig config, int timeoutMillis) throws IOException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(Math.max(1, timeoutMillis));
        switch (config.transport()) {
            case TCP:
                return tcp(requireHost(config), config.port(), false, config.tls(), deadline);
            case TCP_TLS:
                return tcp(requireHost(config), config.port(), true, config.tls(), deadline);
            case HTTP_SOCKET:
                return upgrade(requireUri(config), false, config.tls(), deadline);
            case WEB_SOCKET:
                return upgrade(requireUri(config), true, config.tls(), deadline);
            default:
                throw new NexusTransportException(TransportError.INTERNAL_ERROR,
                    "Unknown transport " + config.transport() + ".", null);
        }
    }

    private static String requireHost(ClientConfig config) {
        String host = config.host();
        if (host == null) {
            throw new NexusTransportException(TransportError.INVALID_ADDRESS, "The configuration has no host.", null);
        }
        return host;
    }

    private static URI requireUri(ClientConfig config) {
        URI uri = config.uri();
        if (uri == null || uri.getHost() == null) {
            throw new NexusTransportException(TransportError.INVALID_ADDRESS, "The configuration has no URI.", null);
        }
        return uri;
    }

    private static Transport tcp(String host, int port, boolean tls, TlsOptions options, long deadline)
        throws IOException {
        Socket socket = open(host, port, deadline);
        if (!tls) {
            return new SocketTransport(socket, socket.getInputStream(), socket.getOutputStream(), false, null,
                "tcp", null);
        }
        SSLSocket secure = secure(socket, host, port, options, deadline);
        return new SocketTransport(secure, secure.getInputStream(), secure.getOutputStream(), true,
            TlsSupport.leafCertificate(secure), "tcptls", null);
    }

    private Transport upgrade(URI uri, boolean webSocket, TlsOptions options, long deadline) throws IOException {
        String scheme = uri.getScheme().toLowerCase(Locale.ROOT);
        boolean tls = scheme.equals("https") || scheme.equals("wss");
        int defaultPort = tls ? 443 : 80;
        int port = uri.getPort() == -1 ? defaultPort : uri.getPort();
        String host = HostnameMatcher.stripBrackets(uri.getHost());
        String hostHeader = hostHeader(uri.getHost(), port, defaultPort);

        Socket socket = open(host, port, deadline);
        @Nullable X509Certificate certificate = null;
        Socket stream = socket;
        try {
            if (tls) {
                SSLSocket secure = secure(socket, host, port, options, deadline);
                certificate = TlsSupport.leafCertificate(secure);
                stream = secure;
            }
            InputStream in = stream.getInputStream();
            OutputStream out = stream.getOutputStream();
            stream.setSoTimeout(remaining(deadline));

            List<String[]> requestHeaders = new ArrayList<>();
            String expectedAccept = null;
            if (webSocket) {
                byte[] nonce = new byte[16];
                RANDOM.nextBytes(nonce);
                String key = Base64Encoder.encode(nonce);
                expectedAccept = acceptFor(key);
                requestHeaders.add(new String[] {"Upgrade", "websocket"});
                requestHeaders.add(new String[] {"Connection", "Upgrade"});
                requestHeaders.add(new String[] {"Sec-WebSocket-Key", key});
                requestHeaders.add(new String[] {"Sec-WebSocket-Version", "13"});
            } else {
                requestHeaders.add(new String[] {"Connection", "Upgrade"});
                requestHeaders.add(new String[] {"Upgrade", "nexnet-httpsockets"});
            }
            requestHeaders.addAll(headers);

            HttpUpgrade.Response response = HttpUpgrade.exchange(in, out,
                HttpUpgrade.request(target(uri), hostHeader, requestHeaders));
            if (response.status != 101) {
                throw HttpUpgrade.failure(response);
            }
            stream.setSoTimeout(0);
            InputStream seeded = response.rest.length == 0 ? in
                : new SocketTransport.PrefixedInputStream(response.rest, in);
            String name = scheme + "://";
            if (!webSocket) {
                return new SocketTransport(stream, seeded, out, tls, certificate, name, null);
            }
            checkWebSocketResponse(response, expectedAccept);
            WebSocketStreams ws = new WebSocketStreams(seeded, out, RANDOM);
            return new SocketTransport(stream, ws.input(), ws.output(), tls, certificate, name,
                reason -> ws.closeQuietly(closeCode(reason)));
        } catch (IOException | RuntimeException e) {
            TlsSupport.closeQuietly(stream);
            TlsSupport.closeQuietly(socket);
            throw e;
        }
    }

    /** The Host header: the URI host (IPv6 in brackets), with the port only when it is not the scheme's default. */
    static String hostHeader(String uriHost, int port, int defaultPort) {
        return port == defaultPort ? uriHost : uriHost + ":" + port;
    }

    private static int closeCode(DisconnectReason reason) {
        return reason == DisconnectReason.PROTOCOL_ERROR ? WebSocketStreams.CLOSE_PROTOCOL_ERROR
            : WebSocketStreams.CLOSE_NORMAL;
    }

    private static void checkWebSocketResponse(HttpUpgrade.Response response, @Nullable String expectedAccept) {
        if (!response.hasToken("upgrade", "websocket")) {
            throw HttpUpgrade.protocolError("The WebSocket upgrade response has no Upgrade: websocket header.");
        }
        if (!response.hasToken("connection", "upgrade")) {
            throw HttpUpgrade.protocolError("The WebSocket upgrade response has no Connection: Upgrade header.");
        }
        String accept = response.header("sec-websocket-accept");
        if (accept == null || !accept.equals(expectedAccept)) {
            throw HttpUpgrade.protocolError("The WebSocket upgrade response has a wrong Sec-WebSocket-Accept.");
        }
        if (response.header("sec-websocket-protocol") != null) {
            throw HttpUpgrade.protocolError("The server selected a WebSocket subprotocol; none was requested.");
        }
        if (response.header("sec-websocket-extensions") != null) {
            throw HttpUpgrade.protocolError("The server selected a WebSocket extension; none was requested.");
        }
    }

    static String acceptFor(String key) {
        try {
            MessageDigest sha1 = MessageDigest.getInstance("SHA-1");
            return Base64Encoder.encode(sha1.digest((key + WEBSOCKET_GUID).getBytes(LATIN1)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException("SHA-1 is not available.", e);
        }
    }

    private String target(URI uri) {
        String path = uri.getRawPath();
        StringBuilder sb = new StringBuilder(path == null || path.isEmpty() ? "/" : path);
        String query = uri.getRawQuery();
        if (query != null && !query.isEmpty()) {
            sb.append('?').append(query);
        }
        if (queryName != null && queryValue != null) {
            sb.append(query != null && !query.isEmpty() ? '&' : '?')
                .append(urlEncode(queryName)).append('=').append(urlEncode(queryValue));
        }
        return sb.toString();
    }

    private static String urlEncode(String value) {
        try {
            // URLEncoder.encode(String, Charset) is Java 10 / API 33; the named-charset form is everywhere.
            return URLEncoder.encode(value, "UTF-8").replace("+", "%20");
        } catch (UnsupportedEncodingException e) {
            throw new IllegalStateException("UTF-8 is not available.", e);
        }
    }

    private static Socket open(String host, int port, long deadline) throws IOException {
        Socket socket = new Socket();
        try {
            socket.setTcpNoDelay(true);
            socket.connect(new InetSocketAddress(HostnameMatcher.stripBrackets(host), port), remaining(deadline));
            return socket;
        } catch (IOException | RuntimeException e) {
            TlsSupport.closeQuietly(socket);
            throw e;
        }
    }

    private static SSLSocket secure(Socket socket, String host, int port, TlsOptions options, long deadline)
        throws IOException {
        try {
            socket.setSoTimeout(remaining(deadline));
            SSLSocket secure = TlsSupport.handshake(socket, host, port, options);
            secure.setSoTimeout(0);
            return secure;
        } catch (IOException | RuntimeException e) {
            TlsSupport.closeQuietly(socket);
            throw e;
        }
    }

    private static int remaining(long deadline) throws SocketTimeoutException {
        long left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime());
        if (left <= 0) {
            throw new SocketTimeoutException("The connection timeout elapsed.");
        }
        return (int) Math.min(Integer.MAX_VALUE, left);
    }

    /** Builds a {@link NexusConnector}. Not thread-safe. */
    public static final class Builder {
        private final List<String[]> headers = new ArrayList<>();
        private @Nullable String queryName;
        private @Nullable String queryValue;

        private Builder() {
        }

        /**
         * Adds a header to the HttpSocket and WebSocket upgrade requests. The headers the protocol needs (Host,
         * Connection, Upgrade, Sec-WebSocket-*) cannot be set.
         *
         * @param name the header name
         * @param value the header value
         * @return this builder
         * @throws IllegalArgumentException for a reserved name or a name or value with CR, LF or other control
         *     characters
         */
        public Builder header(String name, String value) {
            if (name.isEmpty() || !isToken(name)) {
                throw new IllegalArgumentException("Invalid header name: " + name);
            }
            String lower = name.toLowerCase(Locale.ROOT);
            if (lower.equals("host") || lower.equals("connection") || lower.equals("upgrade")
                || lower.startsWith("sec-websocket-") || lower.equals("content-length")
                || lower.equals("transfer-encoding")) {
                throw new IllegalArgumentException("The " + name + " header is set by the transport.");
            }
            for (int i = 0; i < value.length(); i++) {
                char c = value.charAt(i);
                if ((c < 0x20 && c != '\t') || c == 0x7F || c > 0xFF) {
                    throw new IllegalArgumentException("The value of the " + name
                        + " header has a control or non-Latin-1 character.");
                }
            }
            headers.add(new String[] {name, value});
            return this;
        }

        /**
         * Sets the {@code Authorization} header of the upgrade requests, for example {@code "Bearer <token>"}.
         *
         * @param value the header value
         * @return this builder
         */
        public Builder authorization(String value) {
            return header("Authorization", value);
        }

        /**
         * Appends a token to the query string of the upgrade requests, for servers that accept one (NexNet.Asp's
         * opt-in query-string token). Prefer {@link #authorization(String)}: query strings are often logged.
         *
         * @param name the query parameter name
         * @param value the token
         * @return this builder
         */
        public Builder queryToken(String name, String value) {
            if (name.isEmpty()) {
                throw new IllegalArgumentException("The query parameter name is empty.");
            }
            this.queryName = name;
            this.queryValue = value;
            return this;
        }

        /**
         * Builds the connector.
         *
         * @return the connector
         */
        public NexusConnector build() {
            List<String[]> copy = new ArrayList<>();
            for (String[] h : headers) {
                copy.add(h.clone());
            }
            return new NexusConnector(Collections.unmodifiableList(copy), queryName, queryValue);
        }

        private static boolean isToken(String s) {
            for (int i = 0; i < s.length(); i++) {
                char c = s.charAt(i);
                boolean ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || "!#$%&'*+-.^_`|~".indexOf(c) >= 0;
                if (!ok) {
                    return false;
                }
            }
            return true;
        }
    }
}
