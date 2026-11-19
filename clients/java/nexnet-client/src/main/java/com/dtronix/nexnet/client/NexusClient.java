package com.dtronix.nexnet.client;

import com.dtronix.nexnet.CancellationToken;
import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusException;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.NexusTransportException;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.TransportError;
import com.dtronix.nexnet.collections.NexusList;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.pipes.NexusDuplexChannel;
import com.dtronix.nexnet.pipes.NexusDuplexPipe;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SerialExecutor;
import com.dtronix.nexnet.session.SessionClosedException;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.ClientNexusHooks;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import com.dtronix.nexnet.spi.ProxyInvoker;
import com.dtronix.nexnet.transport.NexusConnector;
import com.dtronix.nexnet.transport.Transport;
import java.io.IOException;
import java.net.ConnectException;
import java.net.NoRouteToHostException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.security.cert.X509Certificate;
import java.util.Locale;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Executor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.Function;
import java.util.function.Supplier;
import javax.net.ssl.SSLException;
import org.jspecify.annotations.Nullable;

/**
 * A NexNet client: one connection to a server, with a client nexus that receives the server's invocations and a
 * proxy that invokes the server. Create one with the generated {@code <Export>Client.create(config, nexus)}, which
 * calls {@link #create}.
 *
 * <p>Threading: no method does IO on the calling thread, so all are safe on the Android main thread (except
 * {@link #close()}, which waits a bounded time for the connection's threads). Every connection attempt runs on the
 * runtime's shared pool. Futures and listeners complete on the configured callback executor, never on the
 * connection's reader thread.
 *
 * @param <TProxy> the generated server proxy type
 */
public final class NexusClient<TProxy> implements AutoCloseable {
    private static final AtomicLong IDS = new AtomicLong();

    private final long id;
    private final ClientConfig config;
    private final HandshakeDescriptor handshake;
    private final ClientNexusBase<TProxy> nexus;
    private final TProxy proxy;
    private final Transport.Connector connector;
    private final SessionTimers timers;
    private final SessionOptions sessionOptions;
    private final Executor callbackExecutor;
    private final Executor invocationExecutor;
    private final SerialExecutor listenerExecutor;
    private final @Nullable NexusLogger logger;
    private final CopyOnWriteArrayList<StateListener> listeners = new CopyOnWriteArrayList<>();
    private final AtomicBoolean plaintextWarningLogged = new AtomicBoolean();

    private final Object lock = new Object();
    private ConnectionState state = ConnectionState.DISCONNECTED;
    private @Nullable NexusSession session;
    private @Nullable Attempt attempt;
    private CompletableFuture<Void> disconnected = CompletableFuture.completedFuture(null);
    private boolean closed;

    /** One connection attempt. */
    private static final class Attempt {
        final boolean reconnecting;
        final CompletableFuture<ConnectionResult> result = new CompletableFuture<>();
        final AtomicBoolean finished = new AtomicBoolean();
        final CompletableFuture<Void> disconnected = new CompletableFuture<>();
        volatile boolean canceled;
        volatile boolean established;
        volatile @Nullable NexusSession session;

        Attempt(boolean reconnecting) {
            this.reconnecting = reconnecting;
        }
    }

    private NexusClient(ClientConfig config, HandshakeDescriptor handshake, ClientNexusBase<TProxy> nexus,
                        Function<ProxyInvoker, TProxy> proxyFactory, Transport.Connector connector,
                        SessionTimers timers, SessionOptions sessionOptions) {
        this.id = IDS.incrementAndGet();
        this.config = config;
        this.handshake = handshake;
        this.nexus = nexus;
        this.connector = connector;
        this.timers = timers;
        this.sessionOptions = sessionOptions;
        Executor callbacks = config.callbackExecutor();
        Executor invocations = config.invocationExecutor();
        this.callbackExecutor = callbacks == null ? NexusExecutors.shared() : callbacks;
        this.invocationExecutor = invocations == null ? NexusExecutors.shared() : invocations;
        this.listenerExecutor = new SerialExecutor(callbackExecutor);
        NexusLogger root = config.logger();
        this.logger = root == null ? null : root.createLogger("CL" + id);
        this.proxy = proxyFactory.apply(new UnattachedInvoker());
    }

    /**
     * Creates a client. Called by generated code.
     *
     * @param config the configuration
     * @param handshake the generated handshake constants
     * @param nexus the client nexus
     * @param proxyFactory the generated proxy constructor ({@code ServerProxy::new})
     * @param <TProxy> the proxy type
     * @return the client, not yet connected
     * @throws IllegalStateException when the generated code targets a different runtime major.minor version
     */
    public static <TProxy> NexusClient<TProxy> create(ClientConfig config, HandshakeDescriptor handshake,
                                                      ClientNexusBase<TProxy> nexus,
                                                      Function<ProxyInvoker, TProxy> proxyFactory) {
        return create(config, handshake, nexus, proxyFactory, NexusConnector.defaults());
    }

    /**
     * Creates a client over a custom transport.
     *
     * @param config the configuration
     * @param handshake the generated handshake constants
     * @param nexus the client nexus
     * @param proxyFactory the generated proxy constructor ({@code ServerProxy::new})
     * @param connector opens the transport for each connection attempt
     * @param <TProxy> the proxy type
     * @return the client, not yet connected
     * @throws IllegalStateException when the generated code targets a different runtime major.minor version
     */
    public static <TProxy> NexusClient<TProxy> create(ClientConfig config, HandshakeDescriptor handshake,
                                                      ClientNexusBase<TProxy> nexus,
                                                      Function<ProxyInvoker, TProxy> proxyFactory,
                                                      Transport.Connector connector) {
        return create(config, handshake, nexus, proxyFactory, connector, SessionTimers.system(),
            SessionOptions.defaults());
    }

    static <TProxy> NexusClient<TProxy> create(ClientConfig config, HandshakeDescriptor handshake,
                                               ClientNexusBase<TProxy> nexus,
                                               Function<ProxyInvoker, TProxy> proxyFactory,
                                               Transport.Connector connector, SessionTimers timers,
                                               SessionOptions sessionOptions) {
        if (config == null || handshake == null || nexus == null || proxyFactory == null || connector == null) {
            throw new NullPointerException("An argument is null.");
        }
        if (!sameMajorMinor(handshake.runtimeVersion(), NexusRuntime.VERSION)) {
            throw new IllegalStateException("The generated code targets NexNet " + handshake.runtimeVersion()
                + " but the runtime is " + NexusRuntime.VERSION + "; regenerate it with a matching version.");
        }
        return new NexusClient<>(config, handshake, nexus, proxyFactory, connector, timers, sessionOptions);
    }

    static boolean sameMajorMinor(String a, String b) {
        String[] x = a.split("[.\\-+]");
        String[] y = b.split("[.\\-+]");
        return x.length >= 2 && y.length >= 2 && x[0].equals(y[0]) && x[1].equals(y[1]);
    }

    // ------------------------------------------------------------------ connecting

    /**
     * Connects. Fails when the client is already connected or connecting.
     *
     * @return a future that completes when connected, or fails with the reason
     */
    public CompletableFuture<Void> connect() {
        return connect(CancellationToken.NONE);
    }

    /**
     * Connects, with cancellation.
     *
     * @param cancellationToken cancels the attempt
     * @return a future that completes when connected, or fails with the reason: a
     *     {@link NexusTransportException} for transport and authentication failures, a
     *     {@link CancellationException} when canceled, or a {@link NexusException} naming the disconnect reason when
     *     the server refused the handshake
     */
    public CompletableFuture<Void> connect(CancellationToken cancellationToken) {
        CompletableFuture<Void> future = new CompletableFuture<>();
        Attempt started;
        try {
            started = begin(false, cancellationToken);
        } catch (IllegalStateException e) {
            future.completeExceptionally(e);
            return future;
        }
        started.result.whenComplete((result, error) -> NexusExecutors.execute(callbackExecutor, () -> {
            settleDisconnected(started, result);
            if (result.isSuccess()) {
                future.complete(null);
            } else {
                future.completeExceptionally(toException(result));
            }
        }));
        return future;
    }

    /**
     * Connects and reports the outcome instead of failing.
     *
     * @return a future of the result
     */
    public CompletableFuture<ConnectionResult> tryConnect() {
        return tryConnect(CancellationToken.NONE);
    }

    /**
     * Connects with cancellation and reports the outcome instead of failing.
     *
     * @param cancellationToken cancels the attempt
     * @return a future of the result
     */
    public CompletableFuture<ConnectionResult> tryConnect(CancellationToken cancellationToken) {
        CompletableFuture<ConnectionResult> future = new CompletableFuture<>();
        Attempt started;
        try {
            started = begin(false, cancellationToken);
        } catch (IllegalStateException e) {
            future.complete(new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.NONE, e));
            return future;
        }
        started.result.whenComplete((result, error) -> NexusExecutors.execute(callbackExecutor, () -> {
            settleDisconnected(started, result);
            future.complete(result);
        }));
        return future;
    }

    /** A failed attempt's {@link #disconnected()} future completes before the caller sees the failure. */
    private static void settleDisconnected(Attempt a, ConnectionResult result) {
        if (!result.isSuccess()) {
            a.disconnected.complete(null);
        }
    }

    private Attempt begin(boolean reconnecting, CancellationToken cancellationToken) {
        Attempt a = new Attempt(reconnecting);
        synchronized (lock) {
            if (closed) {
                throw new IllegalStateException("The client is closed.");
            }
            if (attempt != null || session != null) {
                throw new IllegalStateException("The client is already " + (attempt != null ? "connecting." : "connected."));
            }
            attempt = a;
            disconnected = a.disconnected;
        }
        setState(reconnecting ? ConnectionState.RECONNECTING : ConnectionState.CONNECTING);
        CancellationToken token = cancellationToken == null ? CancellationToken.NONE : cancellationToken;
        Registration registration = token.register(() -> cancel(a));
        a.result.whenComplete((r, e) -> registration.close());
        // No IO on the caller thread: the attempt runs on the runtime pool.
        NexusExecutors.execute(NexusExecutors.shared(), () -> {
            ConnectionResult result;
            try {
                result = runAttempt(a);
            } catch (RuntimeException e) {
                result = new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.SOCKET_ERROR, e);
            }
            finishAttempt(a, result);
        });
        return a;
    }

    private void cancel(Attempt a) {
        a.canceled = true;
        NexusSession s = a.session;
        if (s != null && !a.established) {
            s.abort(DisconnectReason.GRACEFUL);
        }
        finishAttempt(a, new ConnectionResult(ConnectionResult.Status.CANCELED, DisconnectReason.NONE,
            new CancellationException("The connection attempt was canceled.")));
    }

    private ConnectionResult runAttempt(Attempt a) {
        if (a.canceled) {
            return canceledResult();
        }
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(config.connectionTimeoutMillis());
        logPlaintextWarningOnce();

        // The token is acquired before the transport connects, so a slow supplier does not run down the server's
        // handshake timeout and a failing one opens no connection.
        byte[] token;
        try {
            token = acquireToken();
        } catch (Throwable e) {
            log(NexusLogLevel.ERROR, "Could not acquire the authentication token.", e);
            return new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.AUTHENTICATION, e);
        }
        if (a.canceled) {
            return canceledResult();
        }

        Transport transport;
        try {
            transport = connector.connect(config, remainingMillis(deadline));
        } catch (NexusTransportException e) {
            return new ConnectionResult(ConnectionResult.Status.DISCONNECTED,
                e.error() == TransportError.AUTHENTICATION_ERROR ? DisconnectReason.AUTHENTICATION
                    : DisconnectReason.SOCKET_ERROR, e);
        } catch (IOException e) {
            NexusTransportException mapped = mapConnectError(e);
            return new ConnectionResult(ConnectionResult.Status.DISCONNECTED,
                mapped.error() == TransportError.AUTHENTICATION_ERROR ? DisconnectReason.AUTHENTICATION
                    : DisconnectReason.SOCKET_ERROR, mapped);
        } catch (RuntimeException e) {
            return new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.SOCKET_ERROR, e);
        }

        NexusSession s = NexusSession.builder(transport, handshake)
            .name(Long.toString(id))
            .authenticationToken(token)
            .reconnecting(a.reconnecting)
            .options(sessionOptions)
            .handshakeTimeoutMillis(config.handshakeTimeoutMillis())
            .timeoutMillis(config.timeoutMillis())
            .pingIntervalMillis(config.pingIntervalMillis())
            .callbackExecutor(callbackExecutor)
            .timers(timers)
            .logger(logger)
            .build();
        synchronized (lock) {
            if (a.canceled) {
                transport.close(DisconnectReason.GRACEFUL);
                return canceledResult();
            }
            a.session = s;
        }
        s.closedInternal().whenComplete((reason, error) ->
            NexusExecutors.execute(NexusExecutors.shared(), () -> onSessionClosed(a, s, reason)));

        try {
            s.start().get(remainingMillis(deadline), TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            s.abort(DisconnectReason.TIMEOUT);
            return new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.TIMEOUT,
                new NexusTransportException(TransportError.CONNECTION_TIMEOUT,
                    "The connection did not complete within " + config.connectionTimeoutMillis() + " ms.", e));
        } catch (ExecutionException e) {
            if (a.canceled) {
                return canceledResult();
            }
            Throwable cause = e.getCause();
            DisconnectReason reason = cause instanceof SessionClosedException
                ? ((SessionClosedException) cause).reason() : s.disconnectReason();
            return new ConnectionResult(ConnectionResult.Status.DISCONNECTED, reason, null);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            s.abort(DisconnectReason.SOCKET_ERROR);
            return new ConnectionResult(ConnectionResult.Status.EXCEPTION, DisconnectReason.SOCKET_ERROR, e);
        }

        ClientNexusHooks.bind(nexus, new Context(s));
        try {
            CompletableFuture.supplyAsync(() -> ClientNexusHooks.connected(nexus, a.reconnecting), invocationExecutor)
                .thenCompose(f -> f)
                .get();
        } catch (ExecutionException e) {
            log(NexusLogLevel.ERROR, "onConnected failed.", e.getCause());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }

        synchronized (lock) {
            if (!a.canceled && s.isConnected()) {
                a.established = true;
                session = s;
            }
        }
        if (!a.established) {
            if (a.canceled) {
                return canceledResult();
            }
            return new ConnectionResult(ConnectionResult.Status.DISCONNECTED, s.disconnectReason(), null);
        }
        return new ConnectionResult(ConnectionResult.Status.SUCCESS, DisconnectReason.NONE, null);
    }

    private static ConnectionResult canceledResult() {
        return new ConnectionResult(ConnectionResult.Status.CANCELED, DisconnectReason.NONE,
            new CancellationException("The connection attempt was canceled."));
    }

    /** Publishes the attempt's outcome once: the client state, the listeners and the attempt future. */
    private void finishAttempt(Attempt a, ConnectionResult result) {
        if (!a.finished.compareAndSet(false, true)) {
            if (result.isSuccess()) {
                // Canceled while completing: the established session is not wanted.
                NexusSession s = a.session;
                if (s != null) {
                    s.disconnect(DisconnectReason.GRACEFUL);
                }
            }
            return;
        }
        ConnectionState next;
        synchronized (lock) {
            if (attempt == a) {
                attempt = null;
            }
            next = result.isSuccess() ? ConnectionState.CONNECTED : ConnectionState.DISCONNECTED;
            if (!result.isSuccess() && session == a.session) {
                session = null;
            }
        }
        if (!result.isSuccess()) {
            NexusSession s = a.session;
            if (s != null) {
                s.abort(DisconnectReason.GRACEFUL);
            }
            ClientNexusHooks.bind(nexus, null);
            NexusExecutors.execute(callbackExecutor, () -> a.disconnected.complete(null));
        }
        setState(next);
        a.result.complete(result);
    }

    private void onSessionClosed(Attempt a, NexusSession s, DisconnectReason reason) {
        synchronized (lock) {
            if (!a.established || session != s) {
                return;
            }
            session = null;
        }
        log(NexusLogLevel.INFORMATION, "Disconnected: " + reason + ".", null);
        try {
            CompletableFuture.supplyAsync(() -> ClientNexusHooks.disconnected(nexus), invocationExecutor)
                .thenCompose(f -> f)
                .get();
        } catch (ExecutionException e) {
            log(NexusLogLevel.ERROR, "onDisconnected failed.", e.getCause());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        ClientNexusHooks.bind(nexus, null);
        setState(ConnectionState.DISCONNECTED);
        NexusExecutors.execute(callbackExecutor, () -> a.disconnected.complete(null));
    }

    private byte[] acquireToken() throws Exception {
        Supplier<byte[]> sync = config.authenticate();
        if (sync != null) {
            byte[] token = sync.get();
            return token == null ? new byte[0] : token;
        }
        Supplier<CompletableFuture<byte[]>> async = config.authenticateAsync();
        if (async != null) {
            CompletableFuture<byte[]> future = async.get();
            if (future == null) {
                return new byte[0];
            }
            try {
                byte[] token = future.get(config.handshakeTimeoutMillis(), TimeUnit.MILLISECONDS);
                return token == null ? new byte[0] : token;
            } catch (TimeoutException e) {
                future.cancel(true);
                throw new NexusTransportException(TransportError.AUTHENTICATION_ERROR,
                    "The authenticate supplier did not complete within the handshake timeout.", e);
            } catch (ExecutionException e) {
                Throwable cause = e.getCause();
                throw cause instanceof Exception ? (Exception) cause : e;
            }
        }
        return new byte[0];
    }

    private static int remainingMillis(long deadline) {
        long left = TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime());
        return (int) Math.max(1, Math.min(Integer.MAX_VALUE, left));
    }

    private static NexusTransportException mapConnectError(IOException e) {
        TransportError error;
        if (e instanceof SSLException) {
            error = TransportError.AUTHENTICATION_ERROR;
        } else if (e instanceof ConnectException) {
            error = TransportError.CONNECTION_REFUSED;
        } else if (e instanceof SocketTimeoutException) {
            error = TransportError.CONNECTION_TIMEOUT;
        } else if (e instanceof NoRouteToHostException) {
            error = TransportError.UNREACHABLE;
        } else if (e instanceof UnknownHostException) {
            error = TransportError.INVALID_ADDRESS;
        } else {
            error = TransportError.INTERNAL_ERROR;
        }
        return new NexusTransportException(error, "Could not connect: " + e.getMessage(), e);
    }

    private static Throwable toException(ConnectionResult result) {
        Throwable exception = result.exception();
        if (exception != null) {
            return exception;
        }
        if (result.disconnectReason() == DisconnectReason.AUTHENTICATION) {
            return new NexusTransportException(TransportError.AUTHENTICATION_ERROR, "Authentication failed.", null);
        }
        return new NexusException("The server closed the connection during the handshake: "
            + result.disconnectReason() + ".");
    }

    private void logPlaintextWarningOnce() {
        if (config.isSecureTransport() || !plaintextWarningLogged.compareAndSet(false, true)) {
            return;
        }
        log(NexusLogLevel.WARNING, "NexNet: unencrypted transport (" + transportName(config)
            + "); credentials and data are visible on the network.", null);
    }

    static String transportName(ClientConfig config) {
        switch (config.transport()) {
            case TCP:
                return "tcp";
            case TCP_TLS:
                return "tcptls";
            default:
                java.net.URI uri = config.uri();
                return (uri == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT)) + "://";
        }
    }

    // ------------------------------------------------------------------ state

    /**
     * Disconnects gracefully. Reconnection does not follow.
     *
     * @return a future that completes when disconnected
     */
    public CompletableFuture<Void> disconnect() {
        NexusSession s;
        Attempt a;
        CompletableFuture<Void> done;
        synchronized (lock) {
            s = session;
            a = attempt;
            done = disconnected;
        }
        if (s != null) {
            setState(ConnectionState.DISCONNECTING);
            s.disconnect(DisconnectReason.GRACEFUL);
        } else if (a != null) {
            cancel(a);
        }
        return done;
    }

    /**
     * The connection state.
     *
     * @return the state
     */
    public ConnectionState state() {
        synchronized (lock) {
            return state;
        }
    }

    private void setState(ConnectionState next) {
        synchronized (lock) {
            if (state == next) {
                return;
            }
            state = next;
        }
        listenerExecutor.execute(() -> {
            for (StateListener listener : listeners) {
                try {
                    listener.onStateChanged(next);
                } catch (RuntimeException e) {
                    log(NexusLogLevel.ERROR, "A state listener failed.", e);
                }
            }
        });
    }

    /**
     * Adds a state listener, called on the callback executor, in order.
     *
     * @param listener the listener
     * @return a registration that removes the listener
     */
    public Registration addStateListener(StateListener listener) {
        if (listener == null) {
            throw new NullPointerException("listener");
        }
        listeners.add(listener);
        return () -> listeners.remove(listener);
    }

    /**
     * Completes when the current connection closes (C# {@code DisconnectedTask}), on the callback executor. Completed
     * when the client is not connected.
     *
     * @return the future
     */
    public CompletableFuture<Void> disconnected() {
        synchronized (lock) {
            return disconnected;
        }
    }

    /**
     * The proxy for invoking the server.
     *
     * @return the proxy
     */
    public TProxy proxy() {
        return proxy;
    }

    /**
     * Whether the transport encrypts the connection (D7). Same as {@link ClientConfig#isSecureTransport()}.
     *
     * @return true for TLS, {@code wss://} and {@code https://}
     */
    public boolean isSecure() {
        return config.isSecureTransport();
    }

    /**
     * The configuration.
     *
     * @return the configuration
     */
    public ClientConfig config() {
        return config;
    }

    /**
     * The current session, for the runtime (invocations, pipes, diagnostics).
     *
     * @return the connected session, or null
     */
    public @Nullable NexusSession session() {
        synchronized (lock) {
            return session;
        }
    }

    /**
     * Creates a pipe to pass to a server method.
     *
     * @return the pipe
     */
    public NexusDuplexPipe createPipe() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Creates a typed channel to pass to a server method.
     *
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     */
    public <T> NexusDuplexChannel<T> createChannel(Formatter<T> itemFormatter) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Disconnects and releases the client's threads, waiting a bounded time. Idempotent. Later connects fail.
     */
    @Override
    public void close() {
        NexusSession s;
        Attempt a;
        synchronized (lock) {
            closed = true;
            s = session;
            a = attempt;
            if (s == null && a != null) {
                s = a.session;
            }
        }
        if (a != null) {
            cancel(a);
        }
        if (s == null) {
            return;
        }
        if (s.isConnected()) {
            setState(ConnectionState.DISCONNECTING);
        }
        CompletableFuture<DisconnectReason> closing = s.disconnect(DisconnectReason.GRACEFUL);
        int timeout = sessionOptions.threadJoinTimeoutMillis() + sessionOptions.disconnectDelayMillis();
        try {
            closing.get(timeout, TimeUnit.MILLISECONDS);
            s.awaitThreads();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (ExecutionException | TimeoutException e) {
            log(NexusLogLevel.DEBUG, "The session did not close in time.", e);
            s.abort(DisconnectReason.GRACEFUL);
        }
    }

    private void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Client", exception, message);
            } catch (RuntimeException ignored) {
                // A failing logger must not break the client.
            }
        }
    }

    /** The session as seen from the client nexus. */
    private final class Context implements ClientSessionContext<TProxy> {
        private final NexusSession session;

        Context(NexusSession session) {
            this.session = session;
        }

        @Override
        public TProxy proxy() {
            return proxy;
        }

        @Override
        public long id() {
            return session.id();
        }

        @Override
        public boolean isSecure() {
            return config.isSecureTransport();
        }

        @Override
        public @Nullable String remoteAddress() {
            return session.transport().remoteAddress();
        }

        @Override
        public int remotePort() {
            return session.transport().remotePort();
        }

        @Override
        public @Nullable X509Certificate remoteCertificate() {
            return session.remoteCertificate();
        }

        @Override
        public NexusDuplexPipe createPipe() {
            return NexusClient.this.createPipe();
        }

        @Override
        public <T> NexusDuplexChannel<T> createChannel(Formatter<T> itemFormatter) {
            return NexusClient.this.createChannel(itemFormatter);
        }

        @Override
        public CompletableFuture<Void> disconnect() {
            return NexusClient.this.disconnect();
        }
    }

    /** The proxy's invoker until the invocation runtime (K07) is attached. */
    private static final class UnattachedInvoker implements ProxyInvoker {
        private static UnsupportedOperationException notImplemented() {
            return new UnsupportedOperationException("not implemented");
        }

        @Override
        public <R> CompletableFuture<R> invoke(int methodId, ArgumentWriter arguments, Formatter<R> result,
                                               @Nullable CancellationToken cancellationToken) {
            throw notImplemented();
        }

        @Override
        public CompletableFuture<Void> invokeTask(int methodId, ArgumentWriter arguments,
                                                  @Nullable CancellationToken cancellationToken) {
            throw notImplemented();
        }

        @Override
        public void invokeVoid(int methodId, ArgumentWriter arguments) {
            throw notImplemented();
        }

        @Override
        public CompletableFuture<Void> invokePipe(int methodId, NexusDuplexPipe pipe, PipeArgumentWriter arguments) {
            throw notImplemented();
        }

        @Override
        public <T> CompletableFuture<Void> invokeChannel(int methodId, NexusDuplexChannel<T> channel,
                                                         PipeArgumentWriter arguments) {
            throw notImplemented();
        }

        @Override
        public <T> NexusList<T> list(int collectionId, Formatter<T> itemFormatter) {
            throw notImplemented();
        }
    }
}
