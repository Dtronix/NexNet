package com.dtronix.nexnet.collections;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNotSame;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.NexusRuntime;
import com.dtronix.nexnet.Registration;
import com.dtronix.nexnet.invocation.ClientProxyInvoker;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.invocation.InvocationRuntime;
import com.dtronix.nexnet.logging.NexusLogLevel;
import com.dtronix.nexnet.pipes.PipeManager;
import com.dtronix.nexnet.pipes.PipeOptions;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.LoopbackTransport;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.NexusSession;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import com.dtronix.nexnet.spi.ClientNexusBase;
import com.dtronix.nexnet.spi.HandshakeDescriptor;
import com.dtronix.nexnet.spi.InvocationContext;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/**
 * The collection client over a real session, invocation runtime and pipe runtime against a scripted server that
 * replays items recorded from the C# runtime (specification section 12).
 */
@Timeout(30)
class NexusListClientTest {
    static final int CLIENT_HASH = -987654321;
    static final HandshakeDescriptor HANDSHAKE =
        new HandshakeDescriptor(NexusRuntime.VERSION, "v2", 123456789, CLIENT_HASH, new int[] {0});
    static final int COLLECTION_ID = 3;
    static final int SERVER_PIPE = 5;

    static final class TestNexus extends ClientNexusBase<Object> {
        @Override
        protected CompletableFuture<Void> dispatch(int methodId, MsgPackReader arguments, InvocationContext context) {
            throw new UnsupportedOperationException();
        }
    }

    final ExecutorService callbacks = Executors.newSingleThreadExecutor(r -> daemon(r, "test-callbacks"));
    final ExecutorService invocations = Executors.newCachedThreadPool(r -> daemon(r, "test-invocations"));
    NexusSession session;
    volatile InvocationRuntime runtime;
    PipeManager pipes;
    CollectionPeer peer;

    static Thread daemon(Runnable runnable, String name) {
        Thread thread = new Thread(runnable, name);
        thread.setDaemon(true);
        return thread;
    }

    void connect() throws Exception {
        LoopbackTransport transport = new LoopbackTransport();
        FakeServer server = transport.server();
        session = NexusSession.builder(transport, HANDSHAKE).name("t").timers(new ManualTimers())
            .options(SessionOptions.defaults().withDisconnectDelayMillis(0).withInvocationCredits(2)).build();
        InvocationRuntime created = InvocationRuntime.builder(session, HANDSHAKE, new TestNexus())
            .options(InvocationOptions.defaults()).codec(CollectionCodec.INSTANCE).callbackExecutor(callbacks)
            .invocationExecutor(invocations).build();
        session.setMessageHandler(created);
        pipes = PipeManager.attach(created, callbacks, SessionTimers.system(), PipeOptions.defaults(), null);
        CompletableFuture<Void> ready = session.start();
        server.readHandshake();
        server.write(FakeServer.PREAMBLE);
        server.write(FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, 32));
        ready.get(5, TimeUnit.SECONDS);
        peer = new CollectionPeer(server);
        created.connected();
        created.startDispatching();
        runtime = created;
    }

    @AfterEach
    void tearDown() throws Exception {
        if (session != null) {
            session.abort(DisconnectReason.GRACEFUL);
            assertTrue(session.awaitThreads(), "threads ended");
            waitFor(() -> pipes.openPipes() == 0);
        }
        if (peer != null) {
            peer.close();
        }
        callbacks.shutdownNow();
        invocations.shutdownNow();
    }

    <T> NexusListClient<T> list(NexusCollectionMode mode, TestFormat<T> format) {
        return new NexusListClient<>(COLLECTION_ID, mode, format, () -> runtime, callbacks, null);
    }

    /** Takes the collection invocation, checks it and answers Ready; returns the full pipe id. */
    int acceptEnable() throws Exception {
        CollectionPeer.Msg invocation = peer.take(MessageTypes.INVOCATION);
        // [invocationId 0, collection id, IgnoreReturn | DuplexPipe, [pipeId]] (specification section 7.6).
        int local = invocation.body[5] & 0xFF;
        assertArrayEquals(new byte[] {(byte) 0x94, 0, COLLECTION_ID, 3, (byte) 0x91, (byte) local}, invocation.body);
        int pipeId = local | (SERVER_PIPE << 8);
        peer.sendState(pipeId, CollectionPeer.READY, 1 << 20);
        return pipeId;
    }

    /** Records the changes delivered to a listener, checking they arrive on the callback executor. */
    static final class Recorder<T> {
        final List<NexusListChange<T>> changes = Collections.synchronizedList(new ArrayList<>());
        final List<String> threads = Collections.synchronizedList(new ArrayList<>());

        Registration attach(NexusList<T> list) {
            return list.addChangeListener(change -> {
                threads.add(Thread.currentThread().getName());
                changes.add(change);
            });
        }

        List<NexusListChange.Action> actions() {
            synchronized (changes) {
                List<NexusListChange.Action> actions = new ArrayList<>();
                for (NexusListChange<T> change : changes) {
                    actions.add(change.action());
                }
                return actions;
            }
        }

        void await(NexusListChange.Action... expected) throws InterruptedException {
            List<NexusListChange.Action> want = Arrays.asList(expected);
            long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
            while (actions().size() < want.size() && System.nanoTime() < deadline) {
                Thread.sleep(5);
            }
            assertEquals(want, actions());
            for (String thread : threads) {
                assertEquals("test-callbacks", thread);
            }
        }

        NexusListChange<T> get(int index) {
            return changes.get(index);
        }

        void clear() {
            changes.clear();
        }
    }

    static byte[] item(String name) {
        return CollectionFixtures.get(name);
    }

    /** {@code [tag, [flags, fields...]]} with integer fields. */
    static byte[] item(int tag, int flags, long... fields) {
        CollectionWire.Out out = new CollectionWire.Out();
        out.arrayHeader(2);
        out.integer(tag);
        out.arrayHeader(1 + fields.length);
        out.integer(flags);
        for (long field : fields) {
            out.integer(field);
        }
        return out.toArray();
    }

    /** Insert or Replace of a long. */
    static byte[] valueItem(int tag, int flags, int version, int index, long value) {
        CollectionWire.Out out = new CollectionWire.Out();
        out.arrayHeader(2);
        out.integer(tag);
        out.arrayHeader(4);
        out.integer(flags);
        out.integer(version);
        out.integer(index);
        out.integer(value);
        return out.toArray();
    }

    /** Syncs a long list to [1, 2, -5] at version 7 with the recorded reset. */
    int enableWithReset(NexusListClient<Long> list) throws Exception {
        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-int"), item("reset-complete"));
        assertTrue(await(enabled));
        await(list.ready());
        return pipeId;
    }

    static <T> T await(CompletableFuture<T> future) throws Exception {
        return future.get(5, TimeUnit.SECONDS);
    }

    static Throwable failure(CompletableFuture<?> future) throws Exception {
        try {
            future.get(5, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            return e.getCause();
        } catch (CancellationException e) {
            return e;
        }
        throw new AssertionError("The future completed normally.");
    }

    static void waitFor(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Condition not met in time.");
            }
            Thread.sleep(5);
        }
    }

    // ------------------------------------------------------------------ enable and reset

    @Test
    void enableInvokesTheCollectionAndCompletesAfterTheReset() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        assertTrue(list.ready().isDone(), "ready is complete before any enable");
        assertTrue(list.disabled().isDone());

        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        CompletableFuture<Void> ready = list.ready();
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-int"));
        Thread.sleep(50);
        assertFalse(enabled.isDone(), "enable waits for ResetComplete");
        assertFalse(ready.isDone());
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        peer.sendItems(pipeId, item("reset-complete"));

        assertTrue(await(enabled));
        await(ready);
        assertEquals(NexusCollectionState.CONNECTED, list.state());
        assertEquals(Arrays.asList(1L, 2L, -5L), list);
        assertEquals(7, list.version());
        events.await(NexusListChange.Action.RESET, NexusListChange.Action.READY);
        assertFalse(list.disabled().isDone());
        // Enabling again while open is the same enable.
        assertTrue(await(list.enable()));
        peer.expectNoItems(50);
    }

    @Test
    void changesBeforeAndDuringTheResetAreReplayedWithTheStaleRule() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.LONG);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        // Insert v8 arrives before ResetStart (not in the snapshot); Clear v5 and Insert v7 during the reset are
        // already in it (version <= the snapshot's 7).
        peer.sendItems(pipeId, item("insert-int"), item("reset-start"), item("clear"), item("reset-values-int"),
            valueItem(CollectionWire.INSERT, 0, 7, 0, 100), item("reset-complete"));
        assertTrue(await(enabled));
        assertEquals(Arrays.asList(1L, 2L, 42L, -5L), list);
        assertEquals(8, list.version());
        // Replayed changes raise their events before the reset's Reset and Ready.
        events.await(NexusListChange.Action.ADD, NexusListChange.Action.RESET, NexusListChange.Action.READY);
        assertEquals(2, events.get(0).index());
        assertEquals(42L, events.get(0).item());
    }

    @Test
    void liveChangesApplyRaiseEventsAndDropStaleOnes() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);

        peer.sendItems(pipeId, item("insert-int"));          // v8: [1, 2, 42, -5]
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 0, 8, 0, 100)); // v8 again: stale, dropped
        peer.sendItems(pipeId, item("replace-int"));         // v11: [1, 99, 42, -5]
        peer.sendItems(pipeId, item("move"));                // v13: [-5, 1, 99, 42]
        peer.sendItems(pipeId, item("clear-ack"));           // v6: stale, dropped; no request is pending
        peer.sendItems(pipeId, item("remove"));              // v15: [-5, 99, 42]
        events.await(NexusListChange.Action.ADD, NexusListChange.Action.REPLACE, NexusListChange.Action.MOVE,
            NexusListChange.Action.REMOVE);
        assertEquals(Arrays.asList(-5L, 99L, 42L), list);
        assertEquals(15, list.version());

        NexusListChange<Long> replace = events.get(1);
        assertEquals(1, replace.index());
        assertEquals(99L, replace.item());
        assertEquals(2L, replace.oldItem());
        NexusListChange<Long> move = events.get(2);
        assertEquals(0, move.index());
        assertEquals(3, move.oldIndex());
        assertEquals(-5L, move.item());
        NexusListChange<Long> remove = events.get(3);
        assertEquals(1, remove.oldIndex());
        assertEquals(1L, remove.oldItem());

        // Version 0 is unversioned (relay) and never stale; the client takes its version.
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 0, 0, 0, 7));
        waitFor(() -> list.size() == 4);
        assertEquals(0, list.version());
        assertEquals(7L, list.get(0));
        peer.sendItems(pipeId, item(CollectionWire.CLEAR, 0, 0));
        waitFor(list::isEmpty);
        events.await(NexusListChange.Action.ADD, NexusListChange.Action.REPLACE, NexusListChange.Action.MOVE,
            NexusListChange.Action.REMOVE, NexusListChange.Action.ADD, NexusListChange.Action.RESET);
    }

    @Test
    void refusedEnableFailsAndCanBeRetried() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        CompletableFuture<Boolean> enabled = list.enable();
        CompletableFuture<Void> ready = list.ready();
        CollectionPeer.Msg invocation = peer.take(MessageTypes.INVOCATION);
        int local = invocation.body[5] & 0xFF;
        // Refused carries the opener's partial id (specification section 7.6).
        peer.sendState(local, CollectionPeer.REFUSED, 0);
        assertInstanceOf(NexusBusyException.class, failure(enabled));
        assertInstanceOf(IllegalStateException.class, failure(ready));
        await(list.disabled());
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        Thread.sleep(50);
        assertTrue(events.actions().isEmpty(), "a failed enable raises nothing");
        waitFor(() -> pipes.openPipes() == 0);

        enableWithReset(list);
        assertEquals(Arrays.asList(1L, 2L, -5L), list);
    }

    @Test
    void enableFailsWhenNotConnected() throws Exception {
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        assertInstanceOf(IllegalStateException.class, failure(list.enable()));
        assertInstanceOf(IllegalStateException.class, failure(list.addAsync(1L)));
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
    }

    // ------------------------------------------------------------------ protocol errors

    private void expectProtocolError(byte[]... items) throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        CompletableFuture<Boolean> enabled = list.enable();
        CompletableFuture<Void> ready = list.ready();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, items);
        // The client closes the pipe; the session stays up.
        assertEquals(CollectionPeer.COMPLETE, peer.takeState(pipeId));
        peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
        assertFalse(await(enabled));
        assertInstanceOf(IllegalStateException.class, failure(ready));
        await(list.disabled());
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        assertTrue(list.isEmpty());
        assertTrue(session.isConnected());
    }

    @Test
    void aNegativeTotalIsAProtocolError() throws Exception {
        expectProtocolError(item("reset-start-negative"));
    }

    @Test
    void moreValuesThanAnnouncedIsAProtocolError() throws Exception {
        expectProtocolError(item(CollectionWire.RESET_START, 0, 7, 2), item("reset-values-int"));
    }

    @Test
    void aValueCountMismatchAtResetCompleteIsAProtocolError() throws Exception {
        expectProtocolError(item(CollectionWire.RESET_START, 0, 7, 4), item("reset-values-int"),
            item("reset-complete"));
    }

    @Test
    void resetItemsOutOfOrderAreProtocolErrors() throws Exception {
        expectProtocolError(item("reset-values-int"));
    }

    @Test
    void aSecondResetStartWhileResettingIsAProtocolError() throws Exception {
        expectProtocolError(item("reset-start"), item("reset-start"));
    }

    @Test
    void aBufferedChangeThatFailsValidationIsAProtocolError() throws Exception {
        // Remove index 1 of an empty snapshot.
        expectProtocolError(item("remove"), item("reset-start-empty"), item("reset-complete"));
    }

    @Test
    void anItemThatDoesNotDecodeClosesThePipe() throws Exception {
        expectProtocolError(CollectionFixtures.hex("920993000000"));
    }

    @Test
    void aChangeThatFailsValidationInReadyClosesThePipeAndAnswersThePendingRequest() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        CompletableFuture<Boolean> add = list.addAsync(9L);
        peer.takeItems(1);
        // An Ack Insert at index 10 of a 3-item list.
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 1, 8, 10, 9));
        assertFalse(await(add));
        assertEquals(CollectionPeer.COMPLETE, peer.takeState(pipeId));
        peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
        await(list.disabled());
        events.await(NexusListChange.Action.RESET);
        assertTrue(list.isEmpty());
        assertEquals(0, list.version());
    }

    // ------------------------------------------------------------------ requests

    @Test
    void requestsCarryTheBaseVersionAndCompleteOnTheirAck() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);

        CompletableFuture<Boolean> add = list.addAsync(42L);
        byte[] request = peer.takeItems(1).get(0);
        // Insert at the count, from the same snapshot: [4, [0, 7, 3, 42]].
        assertArrayEquals(CollectionWire.valueRequest(CollectionWire.INSERT, 7, 3, 42L, CollectionCodec.INSTANCE,
            TestFormat.LONG), request);
        Thread.sleep(50);
        assertFalse(add.isDone(), "the request waits for the server's answer; nothing is applied locally");
        assertEquals(3, list.size());
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 1, 8, 3, 42));
        assertTrue(await(add));
        assertEquals(Arrays.asList(1L, 2L, -5L, 42L), list);
        events.await(NexusListChange.Action.ADD);

        CompletableFuture<Boolean> insert = list.insertAsync(0, 5L);
        assertArrayEquals(CollectionWire.valueRequest(CollectionWire.INSERT, 8, 0, 5L, CollectionCodec.INSTANCE,
            TestFormat.LONG), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 1, 9, 0, 5));
        assertTrue(await(insert));

        CompletableFuture<Boolean> replace = list.replaceAsync(1, -1L);
        assertArrayEquals(CollectionWire.valueRequest(CollectionWire.REPLACE, 9, 1, -1L, CollectionCodec.INSTANCE,
            TestFormat.LONG), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, valueItem(CollectionWire.REPLACE, 1, 10, 1, -1));
        assertTrue(await(replace));

        CompletableFuture<Boolean> move = list.moveAsync(0, 4);
        assertArrayEquals(CollectionWire.moveRequest(10, 0, 4), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, item(CollectionWire.MOVE, 1, 11, 0, 4));
        assertTrue(await(move));

        CompletableFuture<Boolean> removeAt = list.removeAtAsync(0);
        assertArrayEquals(CollectionWire.removeRequest(11, 0), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, item(CollectionWire.REMOVE, 1, 12, 0));
        assertTrue(await(removeAt));

        CompletableFuture<Boolean> clear = list.clearAsync();
        assertArrayEquals(CollectionWire.clearRequest(12), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, item(CollectionWire.CLEAR, 1, 13));
        assertTrue(await(clear));
        assertTrue(list.isEmpty());
        assertEquals(13, list.version());
    }

    @Test
    void oneRequestIsInFlightAndNoopAnswersItWithFalse() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        CompletableFuture<Boolean> first = list.removeAtAsync(0);
        CompletableFuture<Boolean> second = list.addAsync(8L);
        assertArrayEquals(CollectionWire.removeRequest(7, 0), peer.takeItems(1).get(0));
        peer.expectNoItems(100);
        assertFalse(second.isDone());

        // Another session removed the item first: its change (no Ack) arrives, then the Noop for this request.
        peer.sendItems(pipeId, item(CollectionWire.REMOVE, 0, 8, 0), item("noop-ack"));
        assertFalse(await(first));
        // The next request is built from the snapshot current when its turn comes.
        assertArrayEquals(CollectionWire.valueRequest(CollectionWire.INSERT, 8, 2, 8L, CollectionCodec.INSTANCE,
            TestFormat.LONG), peer.takeItems(1).get(0));
        // Any Noop answers the pending request, with or without Ack.
        peer.sendItems(pipeId, item("noop"));
        assertFalse(await(second));
        assertEquals(Arrays.asList(2L, -5L), list);
        // A Noop with nothing pending is ignored.
        peer.sendItems(pipeId, item("noop-ack"), valueItem(CollectionWire.INSERT, 0, 9, 0, 1));
        waitFor(() -> list.size() == 3);
        assertEquals(NexusCollectionState.CONNECTED, list.state());
    }

    @Test
    void removeUsesEqualsAndSendsNothingWhenNoItemMatches() throws Exception {
        connect();
        NexusListClient<String> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.STRING);
        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-string"), item("reset-complete"));
        assertTrue(await(enabled));
        assertEquals(Arrays.asList("a", "hello", null), list);

        assertFalse(await(list.removeAsync("missing")));
        peer.expectNoItems(50);
        // A different instance with the same value: value equality, not identity.
        String hello = new String(new char[] {'h', 'e', 'l', 'l', 'o'});
        CompletableFuture<Boolean> remove = list.removeAsync(hello);
        assertArrayEquals(CollectionWire.removeRequest(7, 1), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, item(CollectionWire.REMOVE, 1, 8, 1));
        assertTrue(await(remove));
        CompletableFuture<Boolean> removeNull = list.removeAsync(null);
        assertArrayEquals(CollectionWire.removeRequest(8, 1), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, item(CollectionWire.REMOVE, 1, 9, 1));
        assertTrue(await(removeNull));
        assertEquals(Collections.singletonList("a"), list);
    }

    @Test
    void readOnlyModesFailMutationsLocally() throws Exception {
        connect();
        for (NexusCollectionMode mode : new NexusCollectionMode[] {NexusCollectionMode.SERVER_TO_CLIENT,
            NexusCollectionMode.RELAY, NexusCollectionMode.UNSET}) {
            NexusListClient<Long> list = list(mode, TestFormat.LONG);
            int pipeId = enableWithReset(list);
            assertInstanceOf(IllegalStateException.class, failure(list.addAsync(1L)));
            assertInstanceOf(IllegalStateException.class, failure(list.clearAsync()));
            assertInstanceOf(IllegalStateException.class, failure(list.removeAsync(1L)));
            peer.expectNoItems(50);
            CompletableFuture<Void> disabled = list.disable();
            assertEquals(CollectionPeer.COMPLETE, peer.takeState(pipeId));
            peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
            await(disabled);
        }
    }

    @Test
    void negativeIndexesAreRejectedLocally() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        enableWithReset(list);
        assertInstanceOf(IndexOutOfBoundsException.class, failure(list.insertAsync(-1, 1L)));
        assertInstanceOf(IndexOutOfBoundsException.class, failure(list.replaceAsync(-1, 1L)));
        assertInstanceOf(IndexOutOfBoundsException.class, failure(list.removeAtAsync(-1)));
        assertInstanceOf(IndexOutOfBoundsException.class, failure(list.moveAsync(0, -1)));
        peer.expectNoItems(50);
    }

    @Test
    void requestsMadeBeforeTheResetWaitForIt() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        CompletableFuture<Boolean> enabled = list.enable();
        CompletableFuture<Boolean> add = list.addAsync(4L);
        int pipeId = acceptEnable();
        peer.expectNoItems(50);
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-int"), item("reset-complete"));
        assertTrue(await(enabled));
        assertArrayEquals(CollectionWire.valueRequest(CollectionWire.INSERT, 7, 3, 4L, CollectionCodec.INSTANCE,
            TestFormat.LONG), peer.takeItems(1).get(0));
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 1, 8, 3, 4));
        assertTrue(await(add));
    }

    // ------------------------------------------------------------------ disable, close and re-enable

    @Test
    void disableClosesThePipeEmptiesTheListAndReEnableResynchronizes() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        CompletableFuture<Boolean> pending = list.addAsync(1L);
        peer.takeItems(1);
        CompletableFuture<Void> disabledFuture = list.disabled();

        CompletableFuture<Void> disabled = list.disable();
        assertEquals(CollectionPeer.COMPLETE, peer.takeState(pipeId));
        peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
        await(disabled);
        await(disabledFuture);
        assertFalse(await(pending), "the pending request completes with false when the pipe closes");
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        assertTrue(list.isEmpty());
        assertEquals(0, list.version());
        events.await(NexusListChange.Action.RESET);
        assertInstanceOf(IllegalStateException.class, failure(list.addAsync(1L)));
        waitFor(() -> pipes.openPipes() == 0);

        // A new pipe and a full reset; nothing carries over.
        CompletableFuture<Boolean> again = list.enable();
        int second = acceptEnable();
        peer.sendItems(second, item(CollectionWire.RESET_START, 0, 20, 1),
            CollectionFixtures.hex("92019200" + "91" + "09"), item("reset-complete"));
        assertTrue(await(again));
        assertEquals(Collections.singletonList(9L), list);
        assertEquals(20, list.version());
    }

    @Test
    void theServerClosingThePipeDisablesTheList() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> events = new Recorder<>();
        events.attach(list);
        peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
        await(list.disabled());
        events.await(NexusListChange.Action.RESET);
        assertTrue(list.isEmpty());
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
    }

    @Test
    void theSessionEndingDisablesTheListWithoutResuming() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        enableWithReset(list);
        CompletableFuture<Boolean> pending = list.addAsync(1L);
        peer.takeItems(1);
        CompletableFuture<Void> disabled = list.disabled();
        session.abort(DisconnectReason.GRACEFUL);
        await(disabled);
        assertFalse(await(pending));
        assertTrue(list.isEmpty());
        assertEquals(NexusCollectionState.DISCONNECTED, list.state());
        // The old session is gone: enabling again fails until a new one is attached.
        assertInstanceOf(IllegalStateException.class, failure(list.enable()));
    }

    @Test
    void anEnableThatEndsBeforeItsResetFailsReady() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        CompletableFuture<Boolean> enabled = list.enable();
        CompletableFuture<Void> ready = list.ready();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, item("reset-start"));
        peer.sendState(pipeId, CollectionPeer.COMPLETE, 0);
        assertFalse(await(enabled));
        assertInstanceOf(IllegalStateException.class, failure(ready));
    }

    // ------------------------------------------------------------------ wiring

    @Test
    void theInvokersCacheOneListPerCollectionBoundToTheCurrentSession() throws Exception {
        connect();
        ClientProxyInvoker invoker = new ClientProxyInvoker(callbacks);
        NexusList<Long> list = invoker.list(COLLECTION_ID, NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        assertSame(list, invoker.list(COLLECTION_ID, NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG));
        assertInstanceOf(NexusListClient.class, list);
        assertInstanceOf(IllegalStateException.class, failure(list.enable()), "not attached to a session");

        invoker.attach(runtime);
        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-int"), item("reset-complete"));
        assertTrue(await(enabled));
        assertEquals(Arrays.asList(1L, 2L, -5L), list);
        CompletableFuture<Boolean> add = list.addAsync(4L);
        peer.takeItems(1);
        peer.sendItems(pipeId, valueItem(CollectionWire.INSERT, 1, 8, 3, 4));
        assertTrue(await(add));

        NexusList<String> direct = runtime.list(4, NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.STRING);
        assertSame(direct, runtime.list(4, NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.STRING));
        assertNotSame(list, runtime.list(COLLECTION_ID, NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG));
        assertSame(NexusCollectionMode.SERVER_TO_CLIENT, ((NexusListClient<String>) direct).mode());
    }

    @Test
    void theInvokersListsLogThroughTheClientsLogger() throws Exception {
        connect();
        List<String> messages = new CopyOnWriteArrayList<>();
        ClientProxyInvoker invoker = new ClientProxyInvoker(callbacks,
            (level, category, exception, message) -> messages.add(level + " " + category + ": " + message));
        invoker.attach(runtime);
        NexusList<Long> list = invoker.list(COLLECTION_ID, NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        CompletableFuture<Boolean> enabled = list.enable();
        int pipeId = acceptEnable();
        peer.sendItems(pipeId, item("reset-start"), item("reset-values-int"), item("reset-complete"));
        assertTrue(await(enabled));
        assertTrue(messages.contains(NexusLogLevel.DEBUG + " Collection" + COLLECTION_ID + ": Connecting collection "
            + COLLECTION_ID + "."), messages.toString());
    }

    // ------------------------------------------------------------------ the List view

    @Test
    void theListViewIsReadOnlyAndReadsASnapshot() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.BI_DIRECTIONAL, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        assertThrows(UnsupportedOperationException.class, () -> list.add(1L));
        assertThrows(UnsupportedOperationException.class, () -> list.add(0, 1L));
        assertThrows(UnsupportedOperationException.class, () -> list.set(0, 1L));
        assertThrows(UnsupportedOperationException.class, () -> list.remove(0));
        assertThrows(UnsupportedOperationException.class, () -> list.remove((Object) 1L));
        assertThrows(UnsupportedOperationException.class, () -> list.addAll(Arrays.asList(1L)));
        assertThrows(UnsupportedOperationException.class, () -> list.removeIf(x -> true));
        assertThrows(UnsupportedOperationException.class, list::clear);
        assertThrows(UnsupportedOperationException.class, () -> list.sort(null));
        assertThrows(UnsupportedOperationException.class, () -> list.iterator().remove());
        assertThrows(UnsupportedOperationException.class, () -> list.subList(0, 1).clear());

        assertTrue(list.contains(-5L));
        assertEquals(1, list.indexOf(2L));
        assertEquals(Arrays.asList(1L, 2L, -5L), list);
        assertEquals(list, Arrays.asList(1L, 2L, -5L));
        assertEquals(Arrays.asList(1L, 2L, -5L).hashCode(), list.hashCode());
        assertEquals("[1, 2, -5]", list.toString());

        // An iterator keeps reading the snapshot it started on.
        Iterator<Long> iterator = list.iterator();
        assertEquals(1L, iterator.next());
        peer.sendItems(pipeId, item("clear-ack"), item(CollectionWire.CLEAR, 0, 8));
        waitFor(list::isEmpty);
        assertEquals(2L, iterator.next());
        assertEquals(-5L, iterator.next());
        assertFalse(iterator.hasNext());
        assertNotEquals(Arrays.asList(1L), list);
    }

    @Test
    void listenersCanBeRemovedAndAFailingListenerDoesNotStopOthers() throws Exception {
        connect();
        NexusListClient<Long> list = list(NexusCollectionMode.SERVER_TO_CLIENT, TestFormat.LONG);
        int pipeId = enableWithReset(list);
        Recorder<Long> first = new Recorder<>();
        Registration registration = first.attach(list);
        list.addChangeListener(change -> {
            throw new IllegalStateException("listener failure");
        });
        Recorder<Long> second = new Recorder<>();
        second.attach(list);
        peer.sendItems(pipeId, item("insert-int"));
        first.await(NexusListChange.Action.ADD);
        second.await(NexusListChange.Action.ADD);
        registration.close();
        registration.close();
        peer.sendItems(pipeId, item("replace-int"));
        second.await(NexusListChange.Action.ADD, NexusListChange.Action.REPLACE);
        assertEquals(Collections.singletonList(NexusListChange.Action.ADD), first.actions());
        assertSame(NexusCollectionMode.SERVER_TO_CLIENT, list.mode());
        assertEquals(COLLECTION_ID, list.collectionId());
    }
}
