package com.dtronix.nexnet.client;

import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.pipes.PipeOptions;
import com.dtronix.nexnet.reconnect.ReconnectionPolicy;
import com.dtronix.nexnet.transport.NexusConnector;
import java.net.URI;
import java.util.Collections;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;
import java.util.function.Supplier;
import org.jspecify.annotations.Nullable;

/**
 * Immutable client configuration: the transport, TLS, authentication, executors and timeouts. Start from one of the
 * transport factories, then {@link Builder#build()}:
 *
 * <pre>{@code
 * ClientConfig config = ClientConfig.webSocket(URI.create("wss://example.com/nexus"))
 *     .authenticate(() -> token)
 *     .build();
 * }</pre>
 *
 * <p>WebSocket and HttpSocket connections can also carry extra upgrade request headers and a query-string token, for
 * servers that authorize the HTTP request itself (NexNet.Asp):
 *
 * <pre>{@code
 * ClientConfig config = ClientConfig.webSocket(URI.create("wss://example.com/nexus"))
 *     .authorization("Bearer " + jwt)
 *     .build();
 * }</pre>
 */
public final class ClientConfig {
    /** Default {@link #connectionTimeoutMillis()}: 50 seconds, as in C#. */
    public static final int DEFAULT_CONNECTION_TIMEOUT_MILLIS = 50_000;
    /** Default {@link #handshakeTimeoutMillis()}: 15 seconds, as in C#. */
    public static final int DEFAULT_HANDSHAKE_TIMEOUT_MILLIS = 15_000;
    /** Default {@link #timeoutMillis()}: 30 seconds, as in C#. */
    public static final int DEFAULT_TIMEOUT_MILLIS = 30_000;
    /**
     * Ping interval used when {@link #pingIntervalMillis()} is null and the server advertises no idle timeout:
     * 10 seconds, as in C#.
     */
    public static final int DEFAULT_PING_INTERVAL_MILLIS = 10_000;
    /** Default {@link #progressTimeoutMillis()}: 30 seconds, as in C# ({@code ConfigBase.ProgressTimeout}). */
    public static final int DEFAULT_PROGRESS_TIMEOUT_MILLIS = 30_000;

    private final TransportKind transport;
    private final @Nullable URI uri;
    private final @Nullable String host;
    private final int port;
    private final TlsOptions tls;
    private final Map<String, String> upgradeHeaders;
    private final @Nullable String queryTokenName;
    private final @Nullable String queryTokenValue;
    private final @Nullable Supplier<byte[]> authenticate;
    private final @Nullable Supplier<CompletableFuture<byte[]>> authenticateAsync;
    private final @Nullable Executor callbackExecutor;
    private final @Nullable Executor invocationExecutor;
    private final @Nullable ReconnectionPolicy reconnectionPolicy;
    private final @Nullable NexusLogger logger;
    private final int connectionTimeoutMillis;
    private final int handshakeTimeoutMillis;
    private final int timeoutMillis;
    private final @Nullable Integer pingIntervalMillis;
    private final int progressTimeoutMillis;
    private final InvocationOptions invocationOptions;
    private final PipeOptions pipeOptions;

    private ClientConfig(Builder b) {
        this.transport = b.transport;
        this.uri = b.uri;
        this.host = b.host;
        this.port = b.port;
        this.tls = b.tls;
        this.upgradeHeaders = Collections.unmodifiableMap(new LinkedHashMap<>(b.upgradeHeaders));
        this.queryTokenName = b.queryTokenName;
        this.queryTokenValue = b.queryTokenValue;
        this.authenticate = b.authenticate;
        this.authenticateAsync = b.authenticateAsync;
        this.callbackExecutor = b.callbackExecutor;
        this.invocationExecutor = b.invocationExecutor;
        this.reconnectionPolicy = b.reconnectionPolicy;
        this.logger = b.logger;
        this.connectionTimeoutMillis = b.connectionTimeoutMillis;
        this.handshakeTimeoutMillis = b.handshakeTimeoutMillis;
        this.timeoutMillis = b.timeoutMillis;
        this.pingIntervalMillis = b.pingIntervalMillis;
        this.progressTimeoutMillis = b.progressTimeoutMillis;
        this.invocationOptions = b.invocationOptions;
        this.pipeOptions = b.pipeOptions;
    }

    /**
     * A WebSocket connection.
     *
     * @param uri a {@code ws://} or {@code wss://} URI
     * @return a builder
     * @throws IllegalArgumentException for another scheme
     */
    public static Builder webSocket(URI uri) {
        return new Builder(TransportKind.WEB_SOCKET, requireScheme(uri, "ws", "wss"), null, -1);
    }

    /**
     * An HttpSocket connection (HTTP/1.1 upgrade to a raw byte stream).
     *
     * @param uri an {@code http://} or {@code https://} URI
     * @return a builder
     * @throws IllegalArgumentException for another scheme
     */
    public static Builder httpSocket(URI uri) {
        return new Builder(TransportKind.HTTP_SOCKET, requireScheme(uri, "http", "https"), null, -1);
    }

    /**
     * A plain TCP connection.
     *
     * @param host the host name or address
     * @param port the port, 1–65535
     * @return a builder
     */
    public static Builder tcp(String host, int port) {
        return new Builder(TransportKind.TCP, null, requireHost(host), requirePort(port));
    }

    /**
     * A TCP connection with TLS.
     *
     * @param host the host name or address; also the default name for SNI and host name verification
     * @param port the port, 1–65535
     * @return a builder
     */
    public static Builder tcpTls(String host, int port) {
        return new Builder(TransportKind.TCP_TLS, null, requireHost(host), requirePort(port));
    }

    /**
     * The transport.
     *
     * @return the transport kind
     */
    public TransportKind transport() {
        return transport;
    }

    /**
     * The URI of a WebSocket or HttpSocket connection.
     *
     * @return the URI, or null for TCP transports
     */
    public @Nullable URI uri() {
        return uri;
    }

    /**
     * The host of a TCP connection.
     *
     * @return the host, or null for URI transports
     */
    public @Nullable String host() {
        return host;
    }

    /**
     * The port of a TCP connection.
     *
     * @return the port, or -1 for URI transports
     */
    public int port() {
        return port;
    }

    /**
     * The TLS options, used for {@link TransportKind#TCP_TLS}, {@code wss://} and {@code https://}.
     *
     * @return the options
     */
    public TlsOptions tls() {
        return tls;
    }

    /**
     * The extra headers of the WebSocket and HttpSocket upgrade requests, in the order they were set. A header that
     * a custom {@link NexusConnector} also sets is sent with the connector's value.
     *
     * @return the headers (name to value), unmodifiable; empty for none and for TCP transports
     */
    public Map<String, String> upgradeHeaders() {
        return upgradeHeaders;
    }

    /**
     * The name of the query parameter that carries the token of the upgrade requests.
     *
     * @return the parameter name, or null for no query-string token
     */
    public @Nullable String queryTokenName() {
        return queryTokenName;
    }

    /**
     * The token appended to the query string of the upgrade requests, as {@code queryTokenName=value}.
     *
     * @return the token, or null for no query-string token
     */
    public @Nullable String queryTokenValue() {
        return queryTokenValue;
    }

    /**
     * Whether the transport encrypts the connection: TCP with TLS, {@code wss://} and {@code https://}. Plain TCP,
     * {@code ws://} and {@code http://} are not secure; the client logs a warning once and reports
     * {@link NexusClient#isSecure()} false (D7).
     *
     * @return true for an encrypted transport
     */
    public boolean isSecureTransport() {
        switch (transport) {
            case TCP_TLS:
                return true;
            case WEB_SOCKET:
            case HTTP_SOCKET:
                String scheme = uri == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
                return scheme.equals("wss") || scheme.equals("https");
            default:
                return false;
        }
    }

    /**
     * The synchronous token supplier, called before every connect and reconnect.
     *
     * @return the supplier, or null
     */
    public @Nullable Supplier<byte[]> authenticate() {
        return authenticate;
    }

    /**
     * The asynchronous token supplier, called before every connect and reconnect and bounded by the handshake
     * timeout.
     *
     * @return the supplier, or null
     */
    public @Nullable Supplier<CompletableFuture<byte[]>> authenticateAsync() {
        return authenticateAsync;
    }

    /**
     * The executor for user-visible callbacks and future completions (for example, the Android main thread).
     *
     * @return the executor, or null for the runtime's shared daemon pool
     */
    public @Nullable Executor callbackExecutor() {
        return callbackExecutor;
    }

    /**
     * The executor that runs incoming invocations of the client nexus.
     *
     * @return the executor, or null for the runtime's shared daemon pool
     */
    public @Nullable Executor invocationExecutor() {
        return invocationExecutor;
    }

    /**
     * The reconnection policy.
     *
     * @return the policy, or null to never reconnect
     */
    public @Nullable ReconnectionPolicy reconnectionPolicy() {
        return reconnectionPolicy;
    }

    /**
     * The logger. Without one the client logs nothing, including the plaintext warning (D7).
     *
     * @return the logger, or null
     */
    public @Nullable NexusLogger logger() {
        return logger;
    }

    /**
     * How long a connection attempt may take, transport and handshake included.
     *
     * @return milliseconds
     */
    public int connectionTimeoutMillis() {
        return connectionTimeoutMillis;
    }

    /**
     * How long the handshake (and the asynchronous authenticate supplier) may take.
     *
     * @return milliseconds
     */
    public int handshakeTimeoutMillis() {
        return handshakeTimeoutMillis;
    }

    /**
     * How long the connection may receive nothing before it is closed with {@code TIMEOUT}.
     *
     * @return milliseconds
     */
    public int timeoutMillis() {
        return timeoutMillis;
    }

    /**
     * The configured ping interval, or null (the default) to derive it from the server. When null, the client pings
     * at a third of the idle timeout the server advertises in its greeting, at least every 1000 ms, and every
     * {@value #DEFAULT_PING_INTERVAL_MILLIS} ms when the server advertises none. A value set here is used as is; if it
     * is not below the server's advertised idle timeout, the client logs a warning on connect, because the server
     * will time the session out between pings. Matches C# {@code ClientConfig.PingInterval}.
     *
     * @return milliseconds, or null to derive the interval
     */
    public @Nullable Integer pingIntervalMillis() {
        return pingIntervalMillis;
    }

    /**
     * How long a large argument or result being received, a pipe whose close the server has not confirmed, or a
     * channel item given extra credit may go without progress before the session disconnects with {@code Stalled}
     * (protocol specification section 9). Checked on every keepalive tick.
     *
     * @return milliseconds
     */
    public int progressTimeoutMillis() {
        return progressTimeoutMillis;
    }

    /**
     * The invocation limits of each session: concurrent invocations, separate-pool invocations, the session receive
     * budget and how much of an exception is sent to the server.
     *
     * @return the options; {@link InvocationOptions#defaults()} unless set
     */
    public InvocationOptions invocationOptions() {
        return invocationOptions;
    }

    /**
     * The pipe limits of each session: how many pipes and channels may be open at once and each one's receive window
     * (C# {@code MaxPipes} and {@code PipeWindow}). A pipe that stops making progress is bounded by
     * {@link #progressTimeoutMillis()}.
     *
     * @return the options
     */
    public PipeOptions pipeOptions() {
        return pipeOptions;
    }

    private static URI requireScheme(URI uri, String plain, String secure) {
        String scheme = uri.getScheme();
        if (scheme == null || !(scheme.equalsIgnoreCase(plain) || scheme.equalsIgnoreCase(secure))) {
            throw new IllegalArgumentException("Expected a " + plain + ":// or " + secure + ":// URI: " + uri);
        }
        if (uri.getHost() == null) {
            throw new IllegalArgumentException("The URI has no host: " + uri);
        }
        return uri;
    }

    private static String requireHost(String host) {
        if (host.isEmpty()) {
            throw new IllegalArgumentException("The host is empty.");
        }
        return host;
    }

    private static int requirePort(int port) {
        if (port < 1 || port > 65535) {
            throw new IllegalArgumentException("The port must be 1 to 65535: " + port);
        }
        return port;
    }

    private static int requirePositive(int value, String name) {
        if (value <= 0) {
            throw new IllegalArgumentException(name + " must be positive: " + value);
        }
        return value;
    }

    /** Builds a {@link ClientConfig}. Not thread-safe. */
    public static final class Builder {
        private final TransportKind transport;
        private final @Nullable URI uri;
        private final @Nullable String host;
        private final int port;
        private TlsOptions tls = TlsOptions.defaults();
        private final Map<String, String> upgradeHeaders = new LinkedHashMap<>();
        private @Nullable String queryTokenName;
        private @Nullable String queryTokenValue;
        private @Nullable Supplier<byte[]> authenticate;
        private @Nullable Supplier<CompletableFuture<byte[]>> authenticateAsync;
        private @Nullable Executor callbackExecutor;
        private @Nullable Executor invocationExecutor;
        private @Nullable ReconnectionPolicy reconnectionPolicy;
        private @Nullable NexusLogger logger;
        private int connectionTimeoutMillis = DEFAULT_CONNECTION_TIMEOUT_MILLIS;
        private int handshakeTimeoutMillis = DEFAULT_HANDSHAKE_TIMEOUT_MILLIS;
        private int timeoutMillis = DEFAULT_TIMEOUT_MILLIS;
        private @Nullable Integer pingIntervalMillis;
        private int progressTimeoutMillis = DEFAULT_PROGRESS_TIMEOUT_MILLIS;
        private InvocationOptions invocationOptions = InvocationOptions.defaults();
        private PipeOptions pipeOptions = PipeOptions.defaults();

        private Builder(TransportKind transport, @Nullable URI uri, @Nullable String host, int port) {
            this.transport = transport;
            this.uri = uri;
            this.host = host;
            this.port = port;
        }

        /**
         * Sets the TLS options.
         *
         * @param tls the options
         * @return this builder
         */
        public Builder tls(TlsOptions tls) {
            if (tls == null) {
                throw new NullPointerException("tls");
            }
            this.tls = tls;
            return this;
        }

        /**
         * Adds a header to the WebSocket or HttpSocket upgrade request, for servers that authorize the HTTP request
         * (NexNet.Asp). Setting a name again (ignoring case) replaces its value. The headers the protocol needs
         * (Host, Connection, Upgrade, Sec-WebSocket-*, Content-Length, Transfer-Encoding) cannot be set.
         *
         * @param name the header name, an HTTP token
         * @param value the header value, Latin-1 without control characters other than tab
         * @return this builder
         * @throws IllegalArgumentException for a reserved name or a name or value with CR, LF or other control
         *     characters
         * @throws IllegalStateException for a TCP transport, which has no upgrade request
         */
        public Builder header(String name, String value) {
            requireUpgradeTransport();
            // The connector's validation, so both ways of setting a header accept the same input.
            NexusConnector.builder().header(name, value);
            for (Iterator<String> it = upgradeHeaders.keySet().iterator(); it.hasNext();) {
                if (it.next().equalsIgnoreCase(name)) {
                    it.remove();
                }
            }
            upgradeHeaders.put(name, value);
            return this;
        }

        /**
         * Sets the {@code Authorization} header of the WebSocket or HttpSocket upgrade request, for example
         * {@code "Bearer <token>"}. The C# client's {@code AuthenticationHeader}.
         *
         * @param value the header value
         * @return this builder
         * @throws IllegalArgumentException for a value with CR, LF or other control characters
         * @throws IllegalStateException for a TCP transport, which has no upgrade request
         */
        public Builder authorization(String value) {
            return header("Authorization", value);
        }

        /**
         * Appends a token to the query string of the WebSocket or HttpSocket upgrade request, URL-encoded, for
         * servers that accept one (NexNet.Asp's opt-in query-string token; browsers cannot send headers). Prefer
         * {@link #authorization(String)}: query strings are often logged.
         *
         * @param name the query parameter name, not empty
         * @param value the token
         * @return this builder
         * @throws IllegalArgumentException for an empty name
         * @throws IllegalStateException for a TCP transport, which has no upgrade request
         */
        public Builder queryToken(String name, String value) {
            requireUpgradeTransport();
            NexusConnector.builder().queryToken(name, value);
            this.queryTokenName = name;
            this.queryTokenValue = value;
            return this;
        }

        private void requireUpgradeTransport() {
            if (transport != TransportKind.WEB_SOCKET && transport != TransportKind.HTTP_SOCKET) {
                throw new IllegalStateException("Upgrade headers and query-string tokens need a WebSocket or "
                    + "HttpSocket transport, not " + transport + ".");
            }
        }

        /**
         * Sends a token in the greeting, from a synchronous supplier called before every connect and reconnect.
         * Mutually exclusive with {@link #authenticateAsync(Supplier)}.
         *
         * @param supplier the token supplier
         * @return this builder
         */
        public Builder authenticate(@Nullable Supplier<byte[]> supplier) {
            this.authenticate = supplier;
            return this;
        }

        /**
         * Sends a token in the greeting, from an asynchronous supplier called before the transport connects, on
         * every connect and reconnect. It must complete within the handshake timeout; if it fails or times out the
         * connect fails with {@code AUTHENTICATION} and no connection is opened. Mutually exclusive with
         * {@link #authenticate(Supplier)}.
         *
         * @param supplier the token supplier
         * @return this builder
         */
        public Builder authenticateAsync(@Nullable Supplier<CompletableFuture<byte[]>> supplier) {
            this.authenticateAsync = supplier;
            return this;
        }

        /**
         * Sets the executor for user-visible callbacks and future completions.
         *
         * @param executor the executor, or null for the shared daemon pool
         * @return this builder
         */
        public Builder callbackExecutor(@Nullable Executor executor) {
            this.callbackExecutor = executor;
            return this;
        }

        /**
         * Sets the executor that runs incoming invocations.
         *
         * @param executor the executor, or null for the shared daemon pool
         * @return this builder
         */
        public Builder invocationExecutor(@Nullable Executor executor) {
            this.invocationExecutor = executor;
            return this;
        }

        /**
         * Sets the reconnection policy.
         *
         * @param policy the policy, or null to never reconnect
         * @return this builder
         */
        public Builder reconnectionPolicy(@Nullable ReconnectionPolicy policy) {
            this.reconnectionPolicy = policy;
            return this;
        }

        /**
         * Sets the logger. Each client logs through {@code logger.createLogger("CL<id>")}.
         *
         * @param logger the logger, or null to log nothing
         * @return this builder
         */
        public Builder logger(@Nullable NexusLogger logger) {
            this.logger = logger;
            return this;
        }

        /**
         * Sets the connection timeout.
         *
         * @param millis milliseconds, positive
         * @return this builder
         */
        public Builder connectionTimeoutMillis(int millis) {
            this.connectionTimeoutMillis = requirePositive(millis, "connectionTimeoutMillis");
            return this;
        }

        /**
         * Sets the handshake timeout.
         *
         * @param millis milliseconds, positive
         * @return this builder
         */
        public Builder handshakeTimeoutMillis(int millis) {
            this.handshakeTimeoutMillis = requirePositive(millis, "handshakeTimeoutMillis");
            return this;
        }

        /**
         * Sets the receive timeout.
         *
         * @param millis milliseconds, positive
         * @return this builder
         */
        public Builder timeoutMillis(int millis) {
            this.timeoutMillis = requirePositive(millis, "timeoutMillis");
            return this;
        }

        /**
         * Sets the ping interval, used as is; null (the default) derives it from the server's advertised idle timeout.
         * See {@link ClientConfig#pingIntervalMillis()}.
         *
         * @param millis milliseconds, positive, or null to derive the interval
         * @return this builder
         * @throws IllegalArgumentException when the value is zero or negative
         */
        public Builder pingIntervalMillis(@Nullable Integer millis) {
            this.pingIntervalMillis = millis == null ? null : requirePositive(millis, "pingIntervalMillis");
            return this;
        }

        /**
         * Sets the progress timeout.
         *
         * @param millis milliseconds, 50 to 600000 (as in C#)
         * @return this builder
         */
        public Builder progressTimeoutMillis(int millis) {
            if (millis < 50 || millis > 600_000) {
                throw new IllegalArgumentException("progressTimeoutMillis must be 50 to 600000: " + millis);
            }
            this.progressTimeoutMillis = millis;
            return this;
        }

        /**
         * Sets the invocation limits, for example
         * {@code InvocationOptions.defaults().withMaxConcurrentInvocations(8)}.
         *
         * @param options the options
         * @return this builder
         */
        public Builder invocationOptions(InvocationOptions options) {
            if (options == null) {
                throw new NullPointerException("options");
            }
            this.invocationOptions = options;
            return this;
        }

        /**
         * Sets the pipe limits, for example {@code PipeOptions.defaults().withMaxPipes(8).withPipeWindow(64 * 1024)}.
         *
         * @param options the options
         * @return this builder
         */
        public Builder pipeOptions(PipeOptions options) {
            if (options == null) {
                throw new NullPointerException("options");
            }
            this.pipeOptions = options;
            return this;
        }

        /**
         * Builds the configuration.
         *
         * @return the configuration
         * @throws IllegalStateException when both authenticate suppliers are set
         */
        public ClientConfig build() {
            if (authenticate != null && authenticateAsync != null) {
                throw new IllegalStateException("Set either authenticate or authenticateAsync, not both.");
            }
            return new ClientConfig(this);
        }
    }
}
