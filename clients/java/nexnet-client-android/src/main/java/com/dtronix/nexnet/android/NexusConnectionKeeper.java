package com.dtronix.nexnet.android;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.os.Handler;
import android.os.Looper;
import androidx.annotation.MainThread;
import androidx.lifecycle.DefaultLifecycleObserver;
import androidx.lifecycle.LifecycleOwner;
import androidx.lifecycle.ProcessLifecycleOwner;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.client.NexusClient;
import com.dtronix.nexnet.reconnect.ReconnectController;
import org.jspecify.annotations.Nullable;

/**
 * Keeps a client connected while the app is in the foreground (D16):
 *
 * <ul>
 *   <li>when the app comes to the foreground ({@link ProcessLifecycleOwner} started), connects the client if it is
 *       disconnected;</li>
 *   <li>when the app goes to the background (stopped), disconnects it, unless
 *       {@link Options#disconnectInBackground(boolean)} turns that off;</li>
 *   <li>when the default network changes ({@link ConnectivityManager#registerDefaultNetworkCallback}), reconnects:
 *       a connected client is disconnected and connected again over the new network, a client waiting to reconnect
 *       retries at once ({@link ReconnectController#retryNow()}), and a disconnected one connects. Turn it off with
 *       {@link Options#reconnectOnNetworkChange(boolean)}.</li>
 * </ul>
 *
 * <pre>{@code
 * NexusConnectionKeeper keeper = new NexusConnectionKeeper(context, client);
 * keeper.start();   // on the main thread, for example in Application.onCreate
 * }</pre>
 *
 * <p>A failed connect is not retried until the next foreground or network change; a lost connection is reconnected by
 * the client's {@code ReconnectionPolicy}. While started, the keeper owns the connection: call {@link #stop()} before
 * disconnecting the client for good. Needs the {@code ACCESS_NETWORK_STATE} permission, which this library's
 * manifest declares. Use from the main thread.
 */
public final class NexusConnectionKeeper implements AutoCloseable {
    private final ConnectivityManager connectivity;
    private final NexusClient<?> client;
    private final Options options;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Observer observer = new Observer();
    private final NetworkWatcher watcher = new NetworkWatcher();
    private boolean started;
    private boolean foreground;
    private @Nullable Network network;

    /**
     * Creates a keeper with the default options.
     *
     * @param context any context; the application context is kept
     * @param client the client to keep connected
     */
    public NexusConnectionKeeper(Context context, NexusClient<?> client) {
        this(context, client, Options.defaults());
    }

    /**
     * Creates a keeper.
     *
     * @param context any context; the application context is kept
     * @param client the client to keep connected
     * @param options the options
     */
    public NexusConnectionKeeper(Context context, NexusClient<?> client, Options options) {
        if (context == null || client == null || options == null) {
            throw new NullPointerException(context == null ? "context" : client == null ? "client" : "options");
        }
        this.connectivity = (ConnectivityManager) context.getApplicationContext()
            .getSystemService(Context.CONNECTIVITY_SERVICE);
        this.client = client;
        this.options = options;
    }

    /**
     * The client kept connected.
     *
     * @return the client
     */
    public NexusClient<?> client() {
        return client;
    }

    /**
     * The options.
     *
     * @return the options
     */
    public Options options() {
        return options;
    }

    /**
     * Whether the keeper is started.
     *
     * @return true between {@link #start()} and {@link #stop()}
     */
    @MainThread
    public boolean isStarted() {
        return started;
    }

    /**
     * Starts following the app lifecycle and the default network; connects at once when the app is in the
     * foreground. Starting again does nothing.
     */
    @MainThread
    public void start() {
        requireMainThread();
        if (started) {
            return;
        }
        started = true;
        ProcessLifecycleOwner.get().getLifecycle().addObserver(observer);
        if (options.reconnectOnNetworkChange && connectivity != null) {
            connectivity.registerDefaultNetworkCallback(watcher);
        }
    }

    /** Stops following the lifecycle and the network. The client keeps its connection. Stopping again does nothing. */
    @MainThread
    public void stop() {
        requireMainThread();
        if (!started) {
            return;
        }
        started = false;
        ProcessLifecycleOwner.get().getLifecycle().removeObserver(observer);
        if (options.reconnectOnNetworkChange && connectivity != null) {
            try {
                connectivity.unregisterNetworkCallback(watcher);
            } catch (IllegalArgumentException ignored) {
                // Not registered.
            }
        }
        network = null;
    }

    /** Same as {@link #stop()}. */
    @Override
    @MainThread
    public void close() {
        stop();
    }

    /** Connects when started, in the foreground and not connected; a pending reconnection retries at once. */
    private void ensureConnected() {
        if (!started || !foreground) {
            return;
        }
        switch (client.state()) {
            case UNSET:
            case DISCONNECTED:
                client.tryConnect();
                break;
            case RECONNECTING:
                ReconnectController reconnection = client.reconnection();
                if (reconnection != null) {
                    reconnection.retryNow();
                }
                break;
            case DISCONNECTING:
                client.disconnected().whenComplete((ignored, error) -> main.post(this::ensureConnected));
                break;
            default:
                break;
        }
    }

    private void onForeground() {
        foreground = true;
        ensureConnected();
    }

    private void onBackground() {
        foreground = false;
        if (!options.disconnectInBackground) {
            return;
        }
        ConnectionState state = client.state();
        if (state == ConnectionState.CONNECTED || state == ConnectionState.CONNECTING
            || state == ConnectionState.RECONNECTING) {
            client.disconnect();
        }
    }

    private void onNetworkAvailable(Network available) {
        if (!started) {
            return;
        }
        Network previous = network;
        network = available;
        if (previous != null && !previous.equals(available) && foreground
            && client.state() == ConnectionState.CONNECTED) {
            // The connection is bound to the old network: move to the new one.
            client.disconnect().whenComplete((ignored, error) -> main.post(this::ensureConnected));
            return;
        }
        ensureConnected();
    }

    private void onNetworkLost(Network lost) {
        if (lost.equals(network)) {
            network = null;
        }
    }

    private static void requireMainThread() {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            throw new IllegalStateException("Call NexusConnectionKeeper on the main thread.");
        }
    }

    /** The process lifecycle: started is the foreground, stopped the background. */
    private final class Observer implements DefaultLifecycleObserver {
        @Override
        public void onStart(LifecycleOwner owner) {
            onForeground();
        }

        @Override
        public void onStop(LifecycleOwner owner) {
            onBackground();
        }
    }

    /** The default network, on the connectivity thread: forwarded to the main thread. */
    private final class NetworkWatcher extends ConnectivityManager.NetworkCallback {
        @Override
        public void onAvailable(Network available) {
            main.post(() -> onNetworkAvailable(available));
        }

        @Override
        public void onLost(Network lost) {
            main.post(() -> onNetworkLost(lost));
        }
    }

    /** What the keeper does on lifecycle and network events. Immutable. */
    public static final class Options {
        private static final Options DEFAULTS = new Options(true, true);

        final boolean disconnectInBackground;
        final boolean reconnectOnNetworkChange;

        private Options(boolean disconnectInBackground, boolean reconnectOnNetworkChange) {
            this.disconnectInBackground = disconnectInBackground;
            this.reconnectOnNetworkChange = reconnectOnNetworkChange;
        }

        /**
         * Disconnect in the background and reconnect on network change.
         *
         * @return the default options
         */
        public static Options defaults() {
            return DEFAULTS;
        }

        /**
         * Whether to disconnect when the app goes to the background (default true). Android may kill a background
         * app's sockets anyway; disconnecting releases the server session at once.
         *
         * @param value true to disconnect
         * @return new options
         */
        public Options disconnectInBackground(boolean value) {
            return new Options(value, reconnectOnNetworkChange);
        }

        /**
         * Whether to follow the default network and reconnect when it changes (default true).
         *
         * @param value true to follow the network
         * @return new options
         */
        public Options reconnectOnNetworkChange(boolean value) {
            return new Options(disconnectInBackground, value);
        }

        /**
         * Whether the keeper disconnects in the background.
         *
         * @return the setting
         */
        public boolean disconnectInBackground() {
            return disconnectInBackground;
        }

        /**
         * Whether the keeper reconnects on network change.
         *
         * @return the setting
         */
        public boolean reconnectOnNetworkChange() {
            return reconnectOnNetworkChange;
        }
    }
}
