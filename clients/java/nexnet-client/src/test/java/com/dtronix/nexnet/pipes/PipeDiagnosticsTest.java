package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.diagnostics.NexusDirection;
import com.dtronix.nexnet.diagnostics.NexusInvocationSnapshot;
import com.dtronix.nexnet.diagnostics.NexusLargeMessageKind;
import com.dtronix.nexnet.diagnostics.NexusLargeMessageSnapshot;
import com.dtronix.nexnet.diagnostics.NexusPartialItemSnapshot;
import com.dtronix.nexnet.diagnostics.NexusPipeSnapshot;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import java.nio.ByteBuffer;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

/** The snapshot values and metrics hooks of the session, invocation and pipe layers (K20), against C# semantics. */
@Timeout(30)
class PipeDiagnosticsTest extends PipeTestBase {
    static final int WINDOW = 1024;
    static final PipeOptions SMALL = PipeOptions.defaults().withPipeWindow(WINDOW);

    /** Every event instrument reported through the session's metrics hook, as text. */
    final List<String> events = new CopyOnWriteArrayList<>();

    void connectMetered(PipeOptions options) throws Exception {
        connectMetered(options, InvocationOptions.defaults());
    }

    void connectMetered(PipeOptions options, InvocationOptions invocationOptions) throws Exception {
        connect(options, invocationOptions, SessionTimers.system(), SessionOptions.defaults());
        session.addMetricsSink((instrument, value, tags) -> {
            assertTrue(value >= 0, instrument.name());
            events.add(instrument.name() + (instrument.kind() == com.dtronix.nexnet.metrics.InstrumentKind.COUNTER
                ? " " + (long) value : "") + " " + tags);
        });
    }

    NexusPipeSnapshot snapshotOf(NexusDuplexPipe pipe) {
        int id = pipe.id() != 0 ? pipe.id() : ((PipeImpl) pipe).localId;
        for (NexusPipeSnapshot snapshot : pipes.snapshots(session.nanoTime())) {
            if (snapshot.id() == id) {
                return snapshot;
            }
        }
        throw new AssertionError("No snapshot of pipe " + id);
    }

    @Test
    void theSessionCountsFramesAndBytesInBothDirections() throws Exception {
        connectMetered(SMALL);
        byte[] greeting = FakeServer.serverGreeting(CLIENT_HASH, 1, 4 * 1024 * 1024, 32);
        assertEquals(1, session.framesReceived());
        assertEquals(FakeServer.PREAMBLE.length + greeting.length, session.bytesReceived());
        assertTrue(session.isEstablished());
        assertTrue(session.hasSent());
        long framesSent = session.framesSent();
        long bytesSent = session.bytesSent();
        assertTrue(framesSent >= 1, "the greeting");
        // A window update for no pipe is ignored; it counts with its header and post-header.
        byte[] window = PipeWire.windowBody(1);
        peer.send(MessageTypes.PIPE_WINDOW_UPDATE, window);
        waitFor(() -> session.framesReceived() == 2);
        assertEquals(FakeServer.PREAMBLE.length + greeting.length
            + Frame.of(MessageTypes.PIPE_WINDOW_UPDATE, window).length(), session.bytesReceived());
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        peer.take(MessageTypes.INVOCATION);
        assertTrue(session.framesSent() > framesSent);
        assertTrue(session.bytesSent() > bytesSent);
        assertFalse(session.isFlushing());
        assertTrue(session.connectedNanos() <= session.lastSentNanos());
    }

    @Test
    void aPipeSnapshotFollowsItsState() throws Exception {
        connectMetered(SMALL);
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        NexusPipeSnapshot created = snapshotOf(pipe);
        assertEquals(pipe.localId, created.id(), "only this side's half until Ready");
        assertFalse(created.isReady());
        assertEquals(WINDOW, created.window());
        assertNull(created.sinceLastData());
        assertNull(created.closing());
        assertNull(created.partialItem());
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        peer.take(MessageTypes.INVOCATION);
        peer.sendState(pipe.localId | (5 << 8), PipeWire.READY, 10);
        await(pipe.ready());
        NexusPipeSnapshot ready = snapshotOf(pipe);
        assertEquals(pipe.localId | (5 << 8), ready.id());
        assertTrue(ready.isReady());
        assertEquals(10, ready.sendCredit());
        assertEquals(WINDOW, ready.receiveCredit());

        // A write larger than the credit waits for more.
        CompletableFuture<Void> write = pipe.write(ByteBuffer.wrap(bytes(30, 1)));
        peer.awaitData(pipe.id(), 10);
        waitFor(() -> snapshotOf(pipe).waitingForCredit() != null);
        NexusPipeSnapshot waiting = snapshotOf(pipe);
        assertEquals(0, waiting.sendCredit());
        assertEquals(20, waiting.waitingBytes());
        assertTrue(events.isEmpty(), events.toString());
        peer.sendWindow(pipe.id(), 20);
        await(write);
        NexusPipeSnapshot granted = snapshotOf(pipe);
        assertNull(granted.waitingForCredit());
        assertEquals(0, granted.waitingBytes());
        waitFor(() -> events.size() == 1);
        assertEquals("nexnet.pipe.credit_wait {}", events.get(0));

        // Data from the server is buffered until read and uses its credit.
        peer.sendData(pipe.id(), bytes(5, 2));
        waitFor(() -> pipe.bufferedBytes() == 5);
        NexusPipeSnapshot data = snapshotOf(pipe);
        assertEquals(5, data.buffered());
        assertEquals(WINDOW - 5, data.receiveCredit());
        assertNotNull(data.sinceLastData());
        assertEquals(0, data.consumedNotGranted());
        await(pipe.read());
        assertEquals(5, snapshotOf(pipe).consumedNotGranted(), "below half the window: not granted yet");

        pipe.close();
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
        NexusPipeSnapshot closing = snapshotOf(pipe);
        assertNotNull(closing.closing());
        assertTrue(closing.writerCompleted());
        assertTrue(closing.readerCompleted());
        peer.sendState(pipe.id(), PipeWire.COMPLETE, 0);
        waitFor(() -> pipes.snapshots(session.nanoTime()).isEmpty());
    }

    @Test
    void refusalsAreReportedLocalAndRemote() throws Exception {
        connectMetered(SMALL.withMaxPipes(1));
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        assertThrows(NexusBusyException.class, () -> pipes.createPipe());
        assertEquals("nexnet.pipe.refusals 1 {nexnet.refusal.side=local}", events.get(0));
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        peer.take(MessageTypes.INVOCATION);
        peer.sendState(pipe.localId, PipeWire.REFUSED, 0);
        waitFor(() -> events.size() == 2);
        assertEquals("nexnet.pipe.refusals 1 {nexnet.refusal.side=remote}", events.get(1));
    }

    /** The server opens a channel to client method 1 with its id {@code serverId}. */
    private NexusDuplexChannel<byte[]> registeredChannel(int serverId) throws Exception {
        CompletableFuture<NexusDuplexChannel<byte[]>> opened = new CompletableFuture<>();
        nexus.methods.put(1, (args, ctx) -> {
            opened.complete(ctx.channel(args[1], TestCodec.BYTES));
            return ctx.result(methodResult());
        });
        invokePipeMethod(1, serverId);
        NexusDuplexChannel<byte[]> channel = await(opened);
        peer.take(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, channel.pipe().id());
        return channel;
    }

    @Test
    void aPartlyArrivedChannelItemAndAResetAreReported() throws Exception {
        connectMetered(SMALL);
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        byte[] stream = framed(bin(200, 1));
        int id = channel.pipe().id();
        peer.sendData(id, stream, 0, 50);
        CompletableFuture<Optional<byte[]>> read = reader.read();
        Thread.sleep(300);
        assertNotNull(snapshotOf(channel.pipe()).partialItem(), () -> snapshotOf(channel.pipe()).toString()
            + " read done " + read.isDone());
        NexusPartialItemSnapshot partial = snapshotOf(channel.pipe()).partialItem();
        assertEquals(stream.length, partial.length());
        assertEquals(50, partial.received());
        peer.sendData(id, stream, 50, 30);
        waitFor(() -> snapshotOf(channel.pipe()).partialItem().received() == 80);
        peer.sendData(id, stream, 80, stream.length - 80);
        assertTrue(await(read).isPresent());
        assertNull(snapshotOf(channel.pipe()).partialItem());

        // An item that is not exactly one MessagePack value resets the pipe.
        peer.sendData(id, new byte[] {2, (byte) 0xC0, (byte) 0xC0});
        assertThrows(Exception.class, () -> await(reader.read()));
        // The first event is the queue wait of the method that registered the channel.
        assertEquals("nexnet.invocation.queue_wait {nexnet.invocation.pool=normal}", events.get(0));
        waitFor(() -> events.size() == 2);
        assertEquals("nexnet.pipe.resets 1 {nexnet.reset.reason=invalid_item}", events.get(1));
    }

    @Test
    void theInvocationSnapshotCountsQueuesAndRecordsTheQueueWait() throws Exception {
        connectMetered(SMALL, InvocationOptions.defaults().withMaxConcurrentInvocations(1));
        CompletableFuture<Void> release = methodResult();
        nexus.methods.put(2, (args, ctx) -> release);
        invoke(1, 2, 0, (byte) 0x90);
        invoke(2, 2, 0, (byte) 0x90);
        waitFor(() -> runtime.snapshot(session.nanoTime()).queued() == 1);
        NexusInvocationSnapshot busy = runtime.snapshot(session.nanoTime());
        assertEquals(1, busy.executing());
        assertEquals(1, busy.maxExecuting());
        assertNotNull(busy.oldestQueuedWait());
        assertNull(busy.separatePoolOldestQueuedWait());
        assertEquals(0, busy.separatePoolQueued());
        // Two credits advertised: one is held by the queued invocation; the running one's came back at dequeue.
        assertEquals(1, busy.inboundCredits());
        assertEquals(32, busy.outboundCredits());
        assertEquals(0, busy.awaitingResults());
        assertEquals(1, runtime.queued(false));
        assertEquals(1, runtime.executing(false));
        assertEquals("nexnet.invocation.queue_wait {nexnet.invocation.pool=normal}", events.get(0));
        release.complete(null);
        waitFor(() -> runtime.snapshot(session.nanoTime()).executing() == 0);
        NexusInvocationSnapshot idle = runtime.snapshot(session.nanoTime());
        assertEquals(0, idle.queued());
        assertNull(idle.oldestQueuedWait());
        assertEquals(2, idle.inboundCredits());
        assertEquals(2, events.size());
    }

    /** {@code MessageStart [invocationId 0, kind, inner type, length]}. */
    private void sendStart(int kind, int innerType, int length) {
        peer.send(MessageTypes.MESSAGE_START, new byte[] {(byte) 0x94, 0, (byte) kind, (byte) innerType,
            (byte) 0xCE, (byte) (length >>> 24), (byte) (length >>> 16), (byte) (length >>> 8), (byte) length});
    }

    @Test
    void largeMessagesInTransferAreListedAndAbortsReported() throws Exception {
        connectMetered(SMALL);
        sendStart(0, MessageTypes.INVOCATION, 100_000);
        // A chunk's post-header (invocation id 0, kind argument) is 0.
        peer.send(MessageTypes.MESSAGE_CHUNK, new byte[1000]);
        waitFor(() -> runtime.largeMessageSnapshots(session.nanoTime()).size() == 1
            && runtime.largeMessageSnapshots(session.nanoTime()).get(0).transferred() == 1000);
        NexusLargeMessageSnapshot inbound = runtime.largeMessageSnapshots(session.nanoTime()).get(0);
        assertEquals(NexusDirection.INBOUND, inbound.direction());
        assertEquals(NexusLargeMessageKind.ARGUMENT, inbound.kind());
        assertEquals(0, inbound.invocationId());
        assertEquals(100_000, inbound.length());
        assertFalse(inbound.aborting());
        assertNotNull(inbound.sinceLastProgress());
        assertEquals(1, runtime.largeMessagesInbound());
        assertEquals(0, runtime.largeMessagesOutbound());
        assertTrue(runtime.budgetSnapshot().reserved() >= 100_000);

        // The server cancels it: an inbound abort.
        peer.send(MessageTypes.MESSAGE_ABORT, new byte[] {(byte) 0x93, 0, 0, 2});
        waitFor(() -> runtime.largeMessagesInbound() == 0);
        assertEquals("nexnet.large_message.aborts 1 {nexnet.direction=inbound, nexnet.abort.reason=cancelled}",
            events.get(0));

        // The server refuses an argument this side sends: an outbound abort.
        peer.send(MessageTypes.MESSAGE_ABORT, new byte[] {(byte) 0x93, 7, 0, 0});
        waitFor(() -> events.size() == 2);
        assertEquals("nexnet.large_message.aborts 1 {nexnet.direction=outbound, nexnet.abort.reason=busy}",
            events.get(1));
        assertTrue(session.isConnected());
    }
}
