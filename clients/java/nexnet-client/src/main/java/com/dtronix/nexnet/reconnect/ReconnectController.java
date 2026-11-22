package com.dtronix.nexnet.reconnect;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.NexusLogger;
import com.dtronix.nexnet.session.NexusExecutors;
import com.dtronix.nexnet.session.SerialExecutor;
import com.dtronix.nexnet.session.SessionTimers;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executor;
import java.util.function.Consumer;
import org.jspecify.annotations.Nullable;

/**
 * Reconnects a client after its session is lost, like the C# {@code NexusClient}: when the client reports a lost
 * session ({@link #onSessionLost}), asks the policy whether the reason qualifies, runs the nexus's
 * {@code onReconnecting()} hook, then waits each policy delay and makes an attempt until one succeeds or the policy
 * returns {@link ReconnectionPolicy#STOP}.
 *
 * <p>Fully asynchronous: delays run on the {@link SessionTimers}, every step and every listener runs on the executor
 * (serialized), and no thread is blocked while waiting. Thread-safe.
 */
public final class ReconnectController implements AutoCloseable {
    private final ReconnectableClient client;
    private final ReconnectionPolicy policy;
    private final SessionTimers timers;
    private final Executor executor;
    private final @Nullable NexusLogger logger;
    private final CopyOnWriteArrayList<ReconnectListener> listeners = new CopyOnWriteArrayList<>();

    private final Object lock = new Object();
    private boolean stopped;
    private boolean running;
    private int attempts;
    private int loop;
    private @Nullable Object delayToken;
    private @Nullable Registration pendingDelay;
    private CompletableFuture<Void> idle = CompletableFuture.completedFuture(null);

    /**
     * Creates a controller on the system timers and the shared runtime pool.
     *
     * @param client the client to reconnect
     * @param policy the policy
     * @param logger the logger, or null
     */
    public ReconnectController(ReconnectableClient client, ReconnectionPolicy policy, @Nullable NexusLogger logger) {
        this(client, policy, SessionTimers.system(), NexusExecutors.shared(), logger);
    }

    /**
     * Creates a controller.
     *
     * @param client the client to reconnect
     * @param policy the policy
     * @param timers the clock and scheduler of the delays
     * @param executor runs the steps and the listeners (serialized)
     * @param logger the logger, or null
     */
    public ReconnectController(ReconnectableClient client, ReconnectionPolicy policy, SessionTimers timers,
                               Executor executor, @Nullable NexusLogger logger) {
        if (client == null || policy == null || timers == null || executor == null) {
            throw new NullPointerException("An argument is null.");
        }
        this.client = client;
        this.policy = policy;
        this.timers = timers;
        this.executor = new SerialExecutor(executor);
        this.logger = logger;
    }

    /**
     * Adds a listener.
     *
     * @param listener the listener
     * @return a registration that removes it
     */
    public Registration addListener(ReconnectListener listener) {
        if (listener == null) {
            throw new NullPointerException("listener");
        }
        listeners.add(listener);
        return () -> listeners.remove(listener);
    }

    /**
     * The policy in use.
     *
     * @return the policy
     */
    public ReconnectionPolicy policy() {
        return policy;
    }

    /**
     * Whether a reconnection loop runs.
     *
     * @return true while waiting or connecting
     */
    public boolean isReconnecting() {
        synchronized (lock) {
            return running;
        }
    }

    /**
     * Whether the loop waits out a policy delay.
     *
     * @return true while waiting
     */
    public boolean isWaiting() {
        synchronized (lock) {
            return delayToken != null;
        }
    }

    /**
     * The attempts made by the current (or last) loop.
     *
     * @return the count
     */
    public int attempts() {
        synchronized (lock) {
            return attempts;
        }
    }

    /**
     * Whether {@link #close()} was called.
     *
     * @return true once stopped
     */
    public boolean isStopped() {
        synchronized (lock) {
            return stopped;
        }
    }

    /**
     * Completes when the current loop ends; completed when none runs.
     *
     * @return the future
     */
    public CompletableFuture<Void> idle() {
        synchronized (lock) {
            return idle;
        }
    }

    /**
     * Whether a session lost for this reason would start a loop: not stopped and the policy accepts the reason. A
     * policy that throws counts as no.
     *
     * @param reason the disconnect reason
     * @return true to reconnect
     */
    public boolean wouldReconnect(DisconnectReason reason) {
        if (isStopped()) {
            return false;
        }
        try {
            return policy.shouldReconnect(reason);
        } catch (RuntimeException e) {
            return false;
        }
    }

    /**
     * Called by the client when an established session closed. Starts a loop unless one runs, the controller is
     * stopped or the policy declines.
     *
     * @param reason why the session closed
     * @return true when a loop started
     */
    public boolean onSessionLost(DisconnectReason reason) {
        return onSessionLost(reason, null);
    }

    /**
     * Called by the client when an established session closed. Starts a loop unless one runs, the controller is
     * stopped or the policy declines. {@code beforeStart} runs on the calling thread once the loop is decided and
     * before its first step is scheduled, so the client can report {@code RECONNECTING} only when a loop starts and
     * before an attempt can connect (K55). It does not run when no loop starts.
     *
     * @param reason why the session closed
     * @param beforeStart runs when a loop starts, before any of its steps; null for none
     * @return true when a loop started
     */
    public boolean onSessionLost(DisconnectReason reason, @Nullable Runnable beforeStart) {
        boolean should;
        try {
            should = policy.shouldReconnect(reason);
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "Reconnection policy shouldReconnect threw.", e);
            should = false;
        }
        if (!should) {
            log(NexusLogLevel.INFORMATION, "Can't reconnect with " + reason + " disconnection reason.", null);
            return false;
        }
        int gen;
        synchronized (lock) {
            if (stopped || running) {
                return false;
            }
            running = true;
            attempts = 0;
            gen = ++loop;
            idle = new CompletableFuture<>();
        }
        ReconnectCounters.RUNNING_LOOPS.incrementAndGet();
        log(NexusLogLevel.INFORMATION, "Connection lost. Reconnecting. " + reason, null);
        if (beforeStart != null) {
            try {
                beforeStart.run();
            } catch (RuntimeException e) {
                log(NexusLogLevel.ERROR, "The reconnection start callback failed.", e);
            }
        }
        executor.execute(() -> {
            notify(l -> l.onReconnecting(reason));
            CompletableFuture<Void> hook;
            try {
                hook = client.onReconnecting();
            } catch (RuntimeException e) {
                hook = failed(e);
            }
            hook.whenComplete((v, e) -> {
                if (e != null) {
                    log(NexusLogLevel.ERROR, "onReconnecting failed.", e);
                }
                executor.execute(() -> next(reason, gen));
            });
        });
        return true;
    }

    /**
     * Ends the pending delay early so the next attempt starts now (for example when the network comes back).
     *
     * @return true when a pending delay was cut short
     */
    public boolean retryNow() {
        Registration delay;
        synchronized (lock) {
            if (delayToken == null) {
                return false;
            }
            delay = pendingDelay;
            delayToken = null;
            pendingDelay = null;
        }
        if (delay != null) {
            delay.close();
        }
        ReconnectCounters.PENDING_DELAYS.decrementAndGet();
        int gen = currentLoop();
        executor.execute(() -> attempt(gen));
        return true;
    }

    /**
     * Ends the current loop, if any: cancels a pending delay and ignores the outcome of an attempt in flight. Later
     * lost sessions start new loops. The client calls it when the user disconnects or closes.
     *
     * @return true when a loop was running
     */
    public boolean cancel() {
        Registration delay;
        boolean waiting;
        synchronized (lock) {
            if (!running) {
                return false;
            }
            loop++;
            waiting = delayToken != null;
            delay = pendingDelay;
            delayToken = null;
            pendingDelay = null;
        }
        if (delay != null) {
            delay.close();
        }
        if (waiting) {
            ReconnectCounters.PENDING_DELAYS.decrementAndGet();
        }
        finish();
        return true;
    }

    private int currentLoop() {
        synchronized (lock) {
            return loop;
        }
    }

    /** Whether the loop is still the current one and the controller runs. Call under the lock. */
    private boolean live(int gen) {
        return !stopped && running && gen == loop;
    }

    /**
     * Stops reconnecting for good: ends the current loop like {@link #cancel()}, and later lost sessions are
     * ignored. Idempotent.
     */
    @Override
    public void close() {
        synchronized (lock) {
            stopped = true;
        }
        cancel();
    }

    /** Asks the policy for the next delay and schedules the attempt. Runs on the executor. */
    private void next(DisconnectReason reason, int gen) {
        int attempt;
        synchronized (lock) {
            if (!live(gen)) {
                return;
            }
            attempt = attempts;
        }
        if (client.state() == ConnectionState.CONNECTED) {
            finish();
            return;
        }
        long delay;
        try {
            delay = policy.reconnectDelayMillis(attempt);
        } catch (RuntimeException e) {
            log(NexusLogLevel.ERROR, "Reconnection policy reconnectDelayMillis threw.", e);
            delay = ReconnectionPolicy.STOP;
        }
        if (delay < 0) {
            if (delay == ReconnectionPolicy.STOP) {
                log(NexusLogLevel.INFORMATION, "Reconnection stopped by the policy after " + attempt + " attempts.",
                    null);
            } else {
                log(NexusLogLevel.ERROR, "Reconnection policy returned an invalid delay: " + delay + ".", null);
            }
            notify(l -> l.onReconnectFailed(reason, attempt));
            finish();
            return;
        }
        Object token = new Object();
        synchronized (lock) {
            if (!live(gen)) {
                return;
            }
            delayToken = token;
            ReconnectCounters.PENDING_DELAYS.incrementAndGet();
        }
        Registration registration = timers.schedule(() -> onDelayElapsed(token, gen), delay);
        synchronized (lock) {
            if (delayToken == token) {
                pendingDelay = registration;
            }
        }
    }

    private void onDelayElapsed(Object token, int gen) {
        synchronized (lock) {
            if (delayToken != token) {
                return;
            }
            delayToken = null;
            pendingDelay = null;
        }
        ReconnectCounters.PENDING_DELAYS.decrementAndGet();
        executor.execute(() -> attempt(gen));
    }

    /** One attempt. Runs on the executor. */
    private void attempt(int gen) {
        int attempt;
        synchronized (lock) {
            if (!live(gen)) {
                return;
            }
            attempt = ++attempts;
        }
        if (client.state() == ConnectionState.CONNECTED) {
            finish();
            return;
        }
        notify(l -> l.onAttempt(attempt));
        log(NexusLogLevel.TRACE, "Reconnection attempt " + attempt, null);
        CompletableFuture<ConnectionResult> result;
        try {
            result = client.reconnect();
        } catch (RuntimeException e) {
            result = failed(e);
        }
        result.whenComplete((r, e) -> executor.execute(() -> {
            synchronized (lock) {
                if (!live(gen)) {
                    return;
                }
            }
            if (r != null && r.isSuccess()) {
                log(NexusLogLevel.INFORMATION, "Reconnected", null);
                notify(l -> l.onReconnected(attempt));
                finish();
                return;
            }
            Throwable error = e != null ? e : r == null ? null : r.exception();
            if (error != null) {
                log(NexusLogLevel.ERROR, "Reconnection attempt " + attempt + " failed.", error);
            }
            DisconnectReason reason = r == null ? DisconnectReason.SOCKET_ERROR : r.disconnectReason();
            next(reason == DisconnectReason.NONE ? DisconnectReason.SOCKET_ERROR : reason, gen);
        }));
    }

    private void finish() {
        CompletableFuture<Void> done;
        synchronized (lock) {
            if (!running) {
                return;
            }
            running = false;
            done = idle;
        }
        ReconnectCounters.RUNNING_LOOPS.decrementAndGet();
        done.complete(null);
    }

    private void notify(Consumer<ReconnectListener> event) {
        for (ReconnectListener listener : listeners) {
            try {
                event.accept(listener);
            } catch (RuntimeException e) {
                log(NexusLogLevel.ERROR, "A reconnection listener failed.", e);
            }
        }
    }

    private static <T> CompletableFuture<T> failed(Throwable e) {
        CompletableFuture<T> f = new CompletableFuture<>();
        f.completeExceptionally(e);
        return f;
    }

    private void log(NexusLogLevel level, String message, @Nullable Throwable exception) {
        NexusLogger current = logger;
        if (current != null && current.isEnabled(level)) {
            try {
                current.log(level, "Reconnect", exception, message);
            } catch (RuntimeException ignored) {
                // A logger must never break reconnection.
            }
        }
    }
}
