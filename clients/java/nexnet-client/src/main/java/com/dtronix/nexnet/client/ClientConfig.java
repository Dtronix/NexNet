package com.dtronix.nexnet.client;

import com.dtronix.nexnet.reconnect.ReconnectionPolicy;
import java.net.URI;
import java.util.Locale;
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
 */
public final class ClientConfig {
    /** Default {@link #connectionTimeoutMillis()}: 50 seconds, as in C#. */
    public static final int DEFAULT_CONNECTION_TIMEOUT_MILLIS = 50_000;
    /** Default {@link #handshakeTimeoutMillis()}: 15 seconds, as in C#. */
    public static final int DEFAULT_HANDSHAKE_TIMEOUT_MILLIS = 15_000;
    /** Default {@link #timeoutMillis()}: 30 seconds, as in C#. */
    public static final int DEFAULT_TIMEOUT_MILLIS = 30_000;
    /** Default {@link #pingIntervalMillis()}: 10 seconds, as in C#. */
    public static final int DEFAULT_PING_INTERVAL_MILLIS = 10_000;

    private final TransportKind transport;
    private final @Nullable URI uri;
    private final @Nullable String host;
    private final int port;
    private final TlsOptions tls;
    private final @Nullable Supplier<byte[]> authenticate;
    private final @Nullable Supplier<CompletableFuture<byte[]>> authenticateAsync;
    private final @Nullable Executor callbackExecutor;
    private final @Nullable Executor invocationExecutor;
    private final @Nullable ReconnectionPolicy reconnectionPolicy;
    private final int connectionTimeoutMillis;
    private final int handshakeTimeoutMillis;
    private final int timeoutMillis;
    private final int pingIntervalMillis;

    private ClientConfig(Builder b) {
        this.transport = b.transport;
        this.uri = b.uri;
        this.host = b.host;
        this.port = b.port;
        this.tls = b.tls;
        this.authenticate = b.authenticate;
        this.authenticateAsync = b.authenticateAsync;
        this.callbackExecutor = b.callbackExecutor;
        this.invocationExecutor = b.invocationExecutor;
        this.reconnectionPolicy = b.reconnectionPolicy;
        this.connectionTimeoutMillis = b.connectionTimeoutMillis;
        this.handshakeTimeoutMillis = b.handshakeTimeoutMillis;
        this.timeoutMillis = b.timeoutMillis;
        this.pingIntervalMillis = b.pingIntervalMillis;
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
     * The ping interval, used when the server does not advertise an idle timeout.
     *
     * @return milliseconds
     */
    public int pingIntervalMillis() {
        return pingIntervalMillis;
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
        private @Nullable Supplier<byte[]> authenticate;
        private @Nullable Supplier<CompletableFuture<byte[]>> authenticateAsync;
        private @Nullable Executor callbackExecutor;
        private @Nullable Executor invocationExecutor;
        private @Nullable ReconnectionPolicy reconnectionPolicy;
        private int connectionTimeoutMillis = DEFAULT_CONNECTION_TIMEOUT_MILLIS;
        private int handshakeTimeoutMillis = DEFAULT_HANDSHAKE_TIMEOUT_MILLIS;
        private int timeoutMillis = DEFAULT_TIMEOUT_MILLIS;
        private int pingIntervalMillis = DEFAULT_PING_INTERVAL_MILLIS;

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
         * Sets the fallback ping interval.
         *
         * @param millis milliseconds, positive
         * @return this builder
         */
        public Builder pingIntervalMillis(int millis) {
            this.pingIntervalMillis = requirePositive(millis, "pingIntervalMillis");
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
