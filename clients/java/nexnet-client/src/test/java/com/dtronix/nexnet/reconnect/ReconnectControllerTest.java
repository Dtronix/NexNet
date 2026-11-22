package com.dtronix.nexnet.reconnect;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.client.ConnectionResult;
import com.dtronix.nexnet.client.ConnectionState;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.logging.RollingLogger;
import com.dtronix.nexnet.session.ManualTimers;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class ReconnectControllerTest {
    /** A client whose attempts complete with queued results; runs everything on the calling thread. */
    static class FakeClient implements ReconnectableClient {
        final List<String> events = new ArrayList<>();
        final ArrayDeque<CompletableFuture<ConnectionResult>> results = new ArrayDeque<>();
        volatile ConnectionState state = ConnectionState.DISCONNECTED;

        @Override
        public ConnectionState state() {
            return state;
        }

        @Override
        public CompletableFuture<Void> onReconnecting() {
            events.add("onReconnecting");
            return CompletableFuture.completedFuture(null);
        }

        @Override
        public CompletableFuture<ConnectionResult> reconnect() {
            events.add("reconnect");
            CompletableFuture<ConnectionResult> next = results.poll();
            if (next == null) {
                next = CompletableFuture.completedFuture(failure());
            }
            next.thenAccept(r -> {
                if (r.isSuccess()) {
                    state = ConnectionState.CONNECTED;
                }
            });
            return next;
        }
    }

    static ConnectionResult failure() {
        return new ConnectionResult(ConnectionResult.Status.DISCONNECTED, DisconnectReason.SOCKET_ERROR, null);
    }

    static ConnectionResult success() {
        return new ConnectionResult(ConnectionResult.Status.SUCCESS, DisconnectReason.NONE, null);
    }

    /** Records listener events. */
    static final class Events implements ReconnectListener {
        final List<String> list = new ArrayList<>();

        @Override
        public void onReconnecting(DisconnectReason reason) {
            list.add("reconnecting:" + reason);
        }

        @Override
        public void onAttempt(int attempt) {
            list.add("attempt:" + attempt);
        }

        @Override
        public void onReconnected(int attempts) {
            list.add("reconnected:" + attempts);
        }

        @Override
        public void onReconnectFailed(DisconnectReason reason, int attempts) {
            list.add("failed:" + reason + ":" + attempts);
        }
    }

    private final ManualTimers timers = new ManualTimers();
    private final FakeClient client = new FakeClient();
    private final Events events = new Events();
    private final RollingLogger logger = new RollingLogger(100, null);

    private ReconnectController controller(ReconnectionPolicy policy) {
        ReconnectController c = new ReconnectController(client, policy, timers, Runnable::run, logger);
        c.addListener(events);
        return c;
    }

    @AfterEach
    void countersReturnToZero() {
        assertEquals(0, ReconnectCounters.pendingDelays());
        assertEquals(0, ReconnectCounters.runningLoops());
    }

    @Test
    void theDefaultPolicyWaits0_2_10_30SecondsThenEvery30() {
        DefaultReconnectionPolicy policy = new DefaultReconnectionPolicy();
        long[] delays = new long[6];
        for (int i = 0; i < delays.length; i++) {
            delays[i] = policy.reconnectDelayMillis(i);
        }
        assertArrayEquals(new long[] {0, 2_000, 10_000, 30_000, 30_000, 30_000}, delays);
        assertTrue(policy.continuous());
        assertEquals(EnumSet.of(DisconnectReason.SOCKET_ERROR, DisconnectReason.TIMEOUT,
            DisconnectReason.SERVER_RESTARTING), policy.reconnectReasons());
        for (DisconnectReason reason : DisconnectReason.values()) {
            assertEquals(policy.reconnectReasons().contains(reason), policy.shouldReconnect(reason), reason.name());
        }
        ReconnectionPolicy lambda = attempt -> 5;
        assertTrue(lambda.shouldReconnect(DisconnectReason.TIMEOUT));
        assertFalse(lambda.shouldReconnect(DisconnectReason.GRACEFUL));

        DefaultReconnectionPolicy once = new DefaultReconnectionPolicy(new long[] {5}, false,
            EnumSet.of(DisconnectReason.SERVER_SHUTDOWN));
        assertEquals(5, once.reconnectDelayMillis(0));
        assertEquals(ReconnectionPolicy.STOP, once.reconnectDelayMillis(1));
        assertTrue(once.shouldReconnect(DisconnectReason.SERVER_SHUTDOWN));
        assertFalse(once.shouldReconnect(DisconnectReason.SOCKET_ERROR));
        assertThrows(IllegalArgumentException.class, () -> new DefaultReconnectionPolicy(new long[0], true));
        assertThrows(IllegalArgumentException.class, () -> new DefaultReconnectionPolicy(new long[] {-1}, true));
    }

    @Test
    void retriesOnThePolicyDelaysUntilAnAttemptSucceeds() {
        client.results.add(CompletableFuture.completedFuture(failure()));
        client.results.add(CompletableFuture.completedFuture(failure()));
        client.results.add(CompletableFuture.completedFuture(success()));
        ReconnectController c = controller(new DefaultReconnectionPolicy());

        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        assertTrue(c.isReconnecting());
        // The first delay is 0: it still goes through the timer.
        assertTrue(c.isWaiting());
        assertEquals(List.of("onReconnecting"), client.events);
        timers.advance(0);
        assertEquals(1, c.attempts());
        assertTrue(c.isWaiting());
        timers.advance(1_999);
        assertEquals(1, c.attempts());
        timers.advance(1);
        assertEquals(2, c.attempts());
        timers.advance(9_999);
        assertEquals(2, c.attempts());
        timers.advance(1);
        assertEquals(3, c.attempts());

        assertFalse(c.isReconnecting());
        assertTrue(c.idle().isDone());
        assertEquals(List.of("reconnecting:SOCKET_ERROR", "attempt:1", "attempt:2", "attempt:3", "reconnected:3"),
            events.list);
        assertEquals(List.of("onReconnecting", "reconnect", "reconnect", "reconnect"), client.events);
        assertEquals(0, timers.pending());
        assertTrue(logger.lines().stream().anyMatch(l -> l.endsWith("Reconnected")));
    }

    @Test
    void doesNotReconnectForReasonsThePolicyDeclines() {
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        assertFalse(c.wouldReconnect(DisconnectReason.GRACEFUL));
        assertTrue(c.wouldReconnect(DisconnectReason.SERVER_RESTARTING));
        assertFalse(c.onSessionLost(DisconnectReason.GRACEFUL));
        assertFalse(c.onSessionLost(DisconnectReason.AUTHENTICATION));
        assertFalse(c.isReconnecting());
        assertTrue(client.events.isEmpty());
        assertTrue(logger.lines().get(0).contains("Can't reconnect with GRACEFUL disconnection reason."));
    }

    @Test
    void beforeStartRunsOnlyWhenALoopStartsAndBeforeItsFirstStep() {
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        List<String> started = new ArrayList<>();
        // A declined reason starts no loop and does not call back (K55).
        assertFalse(c.onSessionLost(DisconnectReason.GRACEFUL, () -> started.add("declined")));
        assertTrue(started.isEmpty());

        // The callback runs before the loop's first step (the onReconnecting hook and listener).
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR,
            () -> started.add("start:" + client.events + ":" + events.list)));
        assertEquals(List.of("start:[]:[]"), started);
        assertEquals(List.of("onReconnecting"), client.events);

        // A second loss while a loop runs is remembered but does not call back yet (K56); closing forgets it, and a
        // stopped controller does not call back.
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR, () -> started.add("running")));
        c.close();
        assertFalse(c.onSessionLost(DisconnectReason.SOCKET_ERROR, () -> started.add("stopped")));
        assertEquals(1, started.size());
    }

    @Test
    void aThrowingBeforeStartIsLoggedAndTheLoopStillStarts() {
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR, () -> {
            throw new IllegalStateException("start");
        }));
        assertTrue(c.isReconnecting());
        assertEquals(List.of("onReconnecting"), client.events);
        assertTrue(logger.lines().stream().anyMatch(l -> l.contains("[ERR]") && l.contains("start callback")));
        c.close();
    }

    @Test
    void stopsWhenThePolicyReturnsStop() {
        ReconnectController c = controller(new DefaultReconnectionPolicy(new long[] {10, 20}, false));
        assertTrue(c.onSessionLost(DisconnectReason.TIMEOUT));
        timers.advance(10);
        timers.advance(20);
        assertFalse(c.isReconnecting());
        assertEquals(List.of("reconnecting:TIMEOUT", "attempt:1", "attempt:2", "failed:SOCKET_ERROR:2"), events.list);
        // A new loss starts a new loop, counting from 0 again.
        assertTrue(c.onSessionLost(DisconnectReason.TIMEOUT));
        assertEquals(0, c.attempts());
        c.close();
        assertFalse(c.isReconnecting());
    }

    @Test
    void closeCancelsThePendingDelayAndLaterLossesAreIgnored() throws Exception {
        ReconnectController c = controller(new DefaultReconnectionPolicy(new long[] {1_000}, true));
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        CompletableFuture<Void> idle = c.idle();
        assertFalse(idle.isDone());
        assertEquals(1, ReconnectCounters.pendingDelays());
        c.close();
        c.close();
        assertTrue(idle.isDone());
        assertTrue(c.isStopped());
        assertEquals(0, timers.pending());
        timers.advance(5_000);
        assertEquals(0, c.attempts());
        assertFalse(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        assertFalse(c.wouldReconnect(DisconnectReason.SOCKET_ERROR));
    }

    @Test
    void cancelDuringAnAttemptIgnoresItsOutcomeAndLaterLossesStartNewLoops() {
        CompletableFuture<ConnectionResult> pending = new CompletableFuture<>();
        client.results.add(pending);
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        assertFalse(c.cancel());
        c.onSessionLost(DisconnectReason.SOCKET_ERROR);
        timers.advance(0);
        assertTrue(c.isReconnecting());
        assertFalse(c.isWaiting());
        CompletableFuture<Void> idle = c.idle();
        assertTrue(c.cancel());
        assertFalse(c.isReconnecting());
        assertTrue(idle.isDone());
        pending.complete(failure());
        assertFalse(c.isReconnecting());
        assertEquals(0, timers.pending());
        assertEquals(List.of("reconnecting:SOCKET_ERROR", "attempt:1"), events.list);

        // Not stopped: the next loss reconnects again; canceling during a delay clears the timer.
        assertTrue(c.onSessionLost(DisconnectReason.TIMEOUT));
        assertTrue(c.isWaiting());
        assertTrue(c.cancel());
        assertEquals(0, timers.pending());
        assertFalse(c.isStopped());
    }

    @Test
    void retryNowCutsTheDelayShort() {
        ReconnectController c = controller(new DefaultReconnectionPolicy(new long[] {60_000}, true));
        assertFalse(c.retryNow());
        c.onSessionLost(DisconnectReason.SOCKET_ERROR);
        assertTrue(c.retryNow());
        assertEquals(1, c.attempts());
        assertEquals(1, timers.pending());
        client.results.add(CompletableFuture.completedFuture(success()));
        assertTrue(c.retryNow());
        assertEquals(2, c.attempts());
        assertFalse(c.isReconnecting());
        assertEquals(0, timers.pending());
    }

    @Test
    void aSecondLossWhileReconnectingDoesNotStartAnotherLoop() {
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        // Remembered for when the loop ends (K56), but no second loop runs now.
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        assertEquals(1, ReconnectCounters.runningLoops());
        assertEquals(List.of("onReconnecting"), client.events);
        c.close();
        assertEquals(List.of("onReconnecting"), client.events);
    }

    @Test
    void aSessionLostBeforeTheLoopEndsStartsANewLoopWhenItEnds() {
        // K56 (C# S49): the attempt connected, and its session is lost while the loop is still ending (here from the
        // onReconnected listener, which runs before the loop ends).
        client.results.add(CompletableFuture.completedFuture(success()));
        client.results.add(CompletableFuture.completedFuture(success()));
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        List<String> started = new ArrayList<>();
        List<Boolean> lostInWindow = new ArrayList<>();
        c.addListener(new ReconnectListener() {
            @Override
            public void onReconnected(int attempts) {
                if (lostInWindow.isEmpty()) {
                    client.state = ConnectionState.DISCONNECTED;
                    lostInWindow.add(c.onSessionLost(DisconnectReason.TIMEOUT,
                        () -> started.add("second:" + c.isReconnecting())));
                    assertEquals(List.of("first"), started);
                }
            }
        });
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR, () -> started.add("first")));
        // The new loop starts before the first loop's idle() completes, so a client waiting on it sees it running.
        List<Boolean> runningWhenFirstEnded = new ArrayList<>();
        c.idle().whenComplete((v, e) -> runningWhenFirstEnded.add(c.isReconnecting()));
        // Both loops run here: the new loop's 0 ms delay elapses within the same advance.
        timers.advance(0);

        assertEquals(List.of(true), lostInWindow);
        assertEquals(List.of(true), runningWhenFirstEnded);
        assertEquals(List.of("first", "second:true"), started);
        assertEquals(List.of("reconnecting:SOCKET_ERROR", "attempt:1", "reconnected:1",
            "reconnecting:TIMEOUT", "attempt:1", "reconnected:1"), events.list);
        assertFalse(c.isReconnecting());
        assertTrue(c.idle().isDone());
        assertEquals(List.of("onReconnecting", "reconnect", "onReconnecting", "reconnect"), client.events);
    }

    @Test
    void aLossDuringTheLoopIsDroppedWhenTheLoopDoesNotConnectOrTheClientIsConnected() {
        // The policy gives up: the remembered loss is not retried (C# parity).
        ReconnectController c = controller(new DefaultReconnectionPolicy(new long[] {10}, false));
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        assertTrue(c.onSessionLost(DisconnectReason.TIMEOUT));
        timers.advance(10);
        assertFalse(c.isReconnecting());
        assertEquals(List.of("onReconnecting", "reconnect"), client.events);

        // The attempt connected and the client is still connected: nothing to reconnect.
        client.results.add(CompletableFuture.completedFuture(success()));
        ReconnectController d = new ReconnectController(client, new DefaultReconnectionPolicy(), timers,
            Runnable::run, logger);
        d.addListener(new ReconnectListener() {
            @Override
            public void onReconnected(int attempts) {
                assertTrue(d.onSessionLost(DisconnectReason.TIMEOUT));
            }
        });
        assertTrue(d.onSessionLost(DisconnectReason.SOCKET_ERROR));
        timers.advance(0);
        assertFalse(d.isReconnecting());
        assertEquals(ConnectionState.CONNECTED, client.state);
        assertEquals(List.of("onReconnecting", "reconnect", "onReconnecting", "reconnect"), client.events);
        assertEquals(0, timers.pending());
    }

    @Test
    void theLoopEndsWhenTheClientIsAlreadyConnected() {
        ReconnectController c = controller(new DefaultReconnectionPolicy());
        c.onSessionLost(DisconnectReason.SOCKET_ERROR);
        client.state = ConnectionState.CONNECTED;
        timers.advance(0);
        assertFalse(c.isReconnecting());
        assertEquals(List.of("onReconnecting"), client.events);
    }

    @Test
    void throwingPoliciesListenersAndAttemptsAreContained() {
        ReconnectionPolicy throwing = new ReconnectionPolicy() {
            @Override
            public long reconnectDelayMillis(int retryAttempt) {
                if (retryAttempt > 0) {
                    throw new IllegalStateException("delay");
                }
                return 0;
            }
        };
        ReconnectableClient failing = new FakeClient() {
            @Override
            public CompletableFuture<ConnectionResult> reconnect() {
                throw new IllegalStateException("attempt");
            }
        };
        ReconnectController c = new ReconnectController(failing, throwing, timers, Runnable::run, logger);
        c.addListener(new ReconnectListener() {
            @Override
            public void onAttempt(int attempt) {
                throw new IllegalStateException("listener");
            }
        });
        c.addListener(events);
        assertTrue(c.onSessionLost(DisconnectReason.SOCKET_ERROR));
        timers.advance(0);
        assertFalse(c.isReconnecting());
        assertEquals(List.of("reconnecting:SOCKET_ERROR", "attempt:1", "failed:SOCKET_ERROR:1"), events.list);
        assertTrue(logger.lines().stream().anyMatch(l -> l.contains("[ERR]") && l.contains("reconnectDelayMillis")));

        ReconnectionPolicy badReason = new ReconnectionPolicy() {
            @Override
            public long reconnectDelayMillis(int retryAttempt) {
                return 0;
            }

            @Override
            public boolean shouldReconnect(DisconnectReason reason) {
                throw new IllegalStateException("reason");
            }
        };
        ReconnectController d = new ReconnectController(client, badReason, timers, Runnable::run, logger);
        assertFalse(d.wouldReconnect(DisconnectReason.SOCKET_ERROR));
        assertFalse(d.onSessionLost(DisconnectReason.SOCKET_ERROR));
        assertTrue(logger.isEnabled(NexusLogLevel.TRACE));
    }
}
