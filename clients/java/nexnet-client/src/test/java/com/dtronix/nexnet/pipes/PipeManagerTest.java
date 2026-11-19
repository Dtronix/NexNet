package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusBusyException;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.session.ManualTimers;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionClosedException;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(30)
class PipeManagerTest extends PipeTestBase {
    static final int WINDOW = 1024;
    static final PipeOptions SMALL = PipeOptions.defaults().withPipeWindow(WINDOW);

    /** Opens a pipe from the client to server method 7; the server answers Ready with its id 5 and a window. */
    private PipeImpl openPipe(long serverWindow) throws Exception {
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        assertEquals(0, pipe.id());
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        ServerPeer.Msg invocation = peer.take(MessageTypes.INVOCATION);
        // [invocationId 0, method 7, IgnoreReturn | DuplexPipe, [localId]]
        assertArrayEquals(new byte[] {(byte) 0x94, 0, 7, 3, (byte) 0x91, (byte) pipe.localId}, invocation.body);
        peer.sendState(pipe.localId | (5 << 8), PipeWire.READY, serverWindow);
        await(pipe.ready());
        assertEquals(pipe.localId | (5 << 8), pipe.id());
        return pipe;
    }

    /** The server opens a pipe to client method 1 (server id 9); returns the client's pipe. */
    private PipeImpl registeredPipe(CompletableFuture<Void> methodDone) throws Exception {
        CompletableFuture<PipeImpl> opened = new CompletableFuture<>();
        nexus.methods.put(1, (args, ctx) -> {
            opened.complete((PipeImpl) ctx.pipe(args[1]));
            return ctx.result(methodDone);
        });
        invokePipeMethod(1, 9);
        PipeImpl pipe = await(opened);
        peer.expectState(pipe.id(), PipeWire.READY, WINDOW);
        assertEquals(pipe.localId | (9 << 8), pipe.id());
        return pipe;
    }

    @Test
    void theOpenerSendsTheLocalIdAndGrantsItsWindowOnReady() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(1000);
        ServerPeer.Msg grant = peer.take(MessageTypes.PIPE_WINDOW_UPDATE, pipe.id());
        assertEquals(WINDOW, grant.window());
        assertEquals(1, pipes.openPipes());
    }

    @Test
    void writesAreSplitAtTheCreditAndCompleteOnceWithinIt() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(1000);
        byte[] data = bytes(2500, 3);
        CompletableFuture<Void> write = pipe.write(ByteBuffer.wrap(data));
        byte[] first = peer.awaitData(pipe.id(), 1000);
        assertEquals(1000, first.length);
        peer.expectNone(MessageTypes.DUPLEX_PIPE_WRITE, 100);
        assertFalse(write.isDone(), "waits for credit");
        peer.sendWindow(pipe.id(), 1500);
        byte[] rest = peer.awaitData(pipe.id(), 1500);
        await(write);
        ByteArrayOutputStream all = new ByteArrayOutputStream();
        all.write(first);
        all.write(rest);
        assertArrayEquals(data, all.toByteArray());
    }

    @Test
    void writesLargerThanAFrameAreChunked() throws Exception {
        connect(PipeOptions.defaults());
        PipeImpl pipe = openPipe(200_000);
        byte[] data = bytes(150_000, 1);
        await(pipe.write(ByteBuffer.wrap(data)));
        int frames = 0;
        ByteArrayOutputStream all = new ByteArrayOutputStream();
        while (all.size() < data.length) {
            ServerPeer.Msg msg = peer.take(MessageTypes.DUPLEX_PIPE_WRITE, pipe.id());
            assertTrue(msg.body.length <= 65535);
            all.write(msg.body);
            frames++;
        }
        assertEquals(3, frames);
        assertArrayEquals(data, all.toByteArray());
    }

    @Test
    void theRegistrantSendsReadyAndGrantsCreditOnlyAsItReads() throws Exception {
        connect(SMALL);
        PipeImpl pipe = registeredPipe(methodResult());
        byte[] data = bytes(WINDOW, 5);
        peer.sendData(pipe.id(), data, 0, 400);
        peer.sendData(pipe.id(), data, 400, WINDOW - 400);
        waitFor(() -> pipe.bufferedBytes() == WINDOW);
        peer.expectNone(MessageTypes.PIPE_WINDOW_UPDATE, 200);
        ByteBuffer first = await(pipe.read());
        assertEquals(400, first.remaining());
        peer.expectNone(MessageTypes.PIPE_WINDOW_UPDATE, 100);
        ByteBuffer second = await(pipe.read());
        assertEquals(WINDOW - 400, second.remaining());
        assertEquals(WINDOW, peer.take(MessageTypes.PIPE_WINDOW_UPDATE, pipe.id()).window());
        byte[] read = new byte[WINDOW];
        first.get(read, 0, 400);
        second.get(read, 400, WINDOW - 400);
        assertArrayEquals(data, read);
    }

    @Test
    void dataBeyondTheCreditDisconnectsWithFlowControl() throws Exception {
        connect(SMALL);
        PipeImpl pipe = registeredPipe(methodResult());
        peer.sendData(pipe.id(), new byte[WINDOW + 1]);
        assertEquals(DisconnectReason.FLOW_CONTROL.code(), peer.awaitDisconnect());
    }

    @Test
    void dataForAnUnknownPipeIsAProtocolError() throws Exception {
        connect(SMALL);
        peer.sendData(0x0901, new byte[3]);
        assertEquals(DisconnectReason.PROTOCOL_ERROR.code(), peer.awaitDisconnect());
    }

    @Test
    void aReadyForNoPendingPipeIsAProtocolError() throws Exception {
        connect(SMALL);
        peer.sendState(0x0503, PipeWire.READY, 100);
        assertEquals(DisconnectReason.PROTOCOL_ERROR.code(), peer.awaitDisconnect());
    }

    @Test
    void windowUpdatesAndStatesForUnknownPipesAreIgnored() throws Exception {
        connect(SMALL);
        peer.sendWindow(0x0503, 100);
        peer.sendState(0x0503, PipeWire.COMPLETE, 0);
        PipeImpl pipe = openPipe(10);
        assertTrue(session.isConnected());
        pipe.close();
    }

    @Test
    void aRefusedPipeFailsWithBusyAndFreesItsId() throws Exception {
        connect(SMALL);
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        peer.take(MessageTypes.INVOCATION);
        peer.sendState(pipe.localId, PipeWire.REFUSED, 0);
        assertInstanceOf(NexusBusyException.class, failure(pipe.ready()));
        await(pipe.completed());
        waitFor(() -> pipes.openPipes() == 0);
        assertInstanceOf(NexusBusyException.class, failure(pipe.write(ByteBuffer.wrap(new byte[1]))));
    }

    @Test
    void theRegistrantRefusesPastMaxPipes() throws Exception {
        connect(SMALL.withMaxPipes(1));
        pipes.createPipe();
        AtomicReference<Throwable> error = new AtomicReference<>();
        CountDownLatch ran = new CountDownLatch(1);
        nexus.methods.put(1, (args, ctx) -> {
            try {
                ctx.pipe(args[1]);
            } catch (RuntimeException e) {
                error.set(e);
            }
            ran.countDown();
            return ctx.noResult();
        });
        invokePipeMethod(1, 9);
        // Refused carries the opener's partial id: the server's byte.
        peer.expectState(9 << 8, PipeWire.REFUSED, 0);
        assertTrue(ran.await(5, TimeUnit.SECONDS));
        assertInstanceOf(NexusBusyException.class, error.get());
        assertThrows(NexusBusyException.class, () -> pipes.createPipe());
    }

    @Test
    void closingSendsCompleteAndFreesOnceThePeerConfirms() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(100);
        pipe.close();
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
        assertEquals(1, pipes.openPipes(), "closing until the peer confirms");
        peer.sendState(pipe.id(), PipeWire.COMPLETE, 0);
        await(pipe.completed());
        waitFor(() -> pipes.openPipes() == 0);
    }

    @Test
    void thePeersCompleteIsAnsweredAndBufferedDataStaysReadable() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(100);
        peer.sendData(pipe.id(), bytes(10, 1));
        peer.sendState(pipe.id(), PipeWire.COMPLETE, 0);
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
        await(pipe.completed());
        assertArrayEquals(bytes(10, 1), toArray(await(pipe.read())));
        assertNull(await(pipe.read()));
        assertEquals(1, pipes.openPipes(), "held until the application releases it");
        pipe.close();
        waitFor(() -> pipes.openPipes() == 0);
    }

    @Test
    void halfClosingSendsTheDirectionBitAfterThePendingData() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(100);
        CompletableFuture<Void> write = pipe.write(ByteBuffer.wrap(bytes(150, 2)));
        CompletableFuture<Void> completed = pipe.completeOutput();
        assertEquals(100, peer.awaitData(pipe.id(), 100).length);
        peer.expectNone(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, 100);
        peer.sendWindow(pipe.id(), 100);
        assertEquals(50, peer.awaitData(pipe.id(), 50).length);
        await(write);
        await(completed);
        peer.expectState(pipe.id(), PipeWire.READY | PipeWire.CLIENT_WRITER_COMPLETE, 0);
        assertInstanceOf(IllegalStateException.class, failure(pipe.write(ByteBuffer.wrap(new byte[1]))));
        // The server completes its direction: the input ends, and both directions are done.
        peer.sendState(pipe.id(), PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        assertNull(await(pipe.read()));
        await(pipe.completed());
    }

    @Test
    void thePeerCompletingOurOutputFailsPendingWrites() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(0);
        CompletableFuture<Void> write = pipe.write(ByteBuffer.wrap(new byte[10]));
        peer.sendState(pipe.id(), PipeWire.READY | PipeWire.CLIENT_WRITER_COMPLETE, 0);
        assertInstanceOf(IllegalStateException.class, failure(write));
        assertTrue(pipe.isOutputClosed());
    }

    @Test
    void aPipeMethodsSlotIsFreedOnceItsPipeOpensAndThePipeClosesWhenItReturns() throws Exception {
        connect(SMALL, InvocationOptions.defaults().withMaxConcurrentInvocations(1), SessionTimers.system(),
            SessionOptions.defaults());
        CompletableFuture<Void> pipeMethod = methodResult();
        PipeImpl pipe = registeredPipe(pipeMethod);
        // A normal invocation runs although the pipe method has not returned.
        nexus.methods.put(2, (args, ctx) -> ctx.result(CompletableFuture.completedFuture(null)));
        invoke(4, 2, 0, (byte) 0x90);
        ServerPeer.Msg result = peer.take(MessageTypes.INVOCATION_RESULT);
        assertEquals(4, result.body[1], "the invocation id of the result");
        assertEquals(1, pipes.openPipes());
        pipeMethod.complete(null);
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
        peer.sendState(pipe.id(), PipeWire.COMPLETE, 0);
        waitFor(() -> pipes.openPipes() == 0);
    }

    @Test
    void theSessionEndingCompletesEverythingAndFreesThePipes() throws Exception {
        connect(SMALL);
        PipeImpl open = openPipe(0);
        PipeImpl pending = (PipeImpl) pipes.createPipe();
        await(pipes.invokePipe(7, pending, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        CompletableFuture<ByteBuffer> read = open.read();
        CompletableFuture<Void> write = open.write(ByteBuffer.wrap(new byte[5]));
        assertEquals(2, pipes.openPipes());
        session.abort(DisconnectReason.GRACEFUL);
        assertNull(await(read));
        assertInstanceOf(SessionClosedException.class, failure(write));
        assertInstanceOf(SessionClosedException.class, failure(pending.ready()));
        await(open.completed());
        waitFor(() -> pipes.openPipes() == 0);
        assertThrows(IllegalStateException.class, () -> pipes.createPipe());
    }

    @Test
    void closingBeforeReadyClosesOnceTheServerAnswers() throws Exception {
        connect(SMALL);
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        peer.take(MessageTypes.INVOCATION);
        pipe.close();
        failure(pipe.ready());
        int fullId = pipe.localId | (5 << 8);
        peer.sendState(fullId, PipeWire.READY, 100);
        peer.expectState(fullId, PipeWire.COMPLETE, 0);
        peer.sendState(fullId, PipeWire.COMPLETE, 0);
        waitFor(() -> pipes.openPipes() == 0);
        assertTrue(session.isConnected());
    }

    @Test
    void aPipeCanBePassedToOnlyOneMethod() throws Exception {
        connect(SMALL);
        PipeImpl pipe = (PipeImpl) pipes.createPipe();
        await(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x91, (byte) id}).write(w)));
        assertInstanceOf(IllegalStateException.class,
            failure(pipes.invokePipe(7, pipe, (w, id) -> TestCodec.args(new byte[] {(byte) 0x90}).write(w))));
    }

    @Test
    void aClosingPipeThePeerNeverConfirmsStallsTheSession() throws Exception {
        ManualTimers timers = new ManualTimers();
        connect(SMALL.withProgressTimeoutMillis(1000), InvocationOptions.defaults(), timers,
            SessionOptions.defaults());
        PipeImpl pipe = openPipe(10);
        pipe.close();
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
        timers.advance(900);
        assertTrue(session.isConnected());
        timers.advance(400);
        assertEquals(DisconnectReason.STALLED.code(), peer.awaitDisconnect());
    }

    @Test
    void streamsAdaptReadsAndWrites() throws Exception {
        connect(SMALL);
        PipeImpl pipe = openPipe(10_000);
        peer.sendData(pipe.id(), bytes(300, 4));
        peer.sendState(pipe.id(), PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        InputStream in = pipe.inputStream();
        ByteArrayOutputStream read = new ByteArrayOutputStream();
        byte[] buffer = new byte[64];
        for (int n; (n = in.read(buffer)) >= 0; ) {
            read.write(buffer, 0, n);
        }
        assertArrayEquals(bytes(300, 4), read.toByteArray());
        try (OutputStream out = pipe.outputStream()) {
            out.write(bytes(500, 6));
            out.write(1);
        }
        byte[] sent = peer.awaitData(pipe.id(), 501);
        assertEquals(501, sent.length);
        peer.expectState(pipe.id(), PipeWire.COMPLETE, 0);
    }

    @Test
    void forwardToCopiesUntilTheSourceEndsThenHalfClosesTheDestination() throws Exception {
        connect(SMALL);
        PipeImpl destination = openPipe(100);
        PipeImpl source = registeredPipe(methodResult());
        CompletableFuture<Void> forward = source.forwardTo(destination);
        byte[] data = bytes(400, 8);
        peer.sendData(source.id(), data, 0, 200);
        peer.sendData(source.id(), data, 200, 200);
        assertEquals(100, peer.awaitData(destination.id(), 100).length);
        // The destination has no credit left: the second buffer is not read, so the source grants nothing.
        Thread.sleep(100);
        assertEquals(200, source.bufferedBytes());
        peer.expectNone(MessageTypes.PIPE_WINDOW_UPDATE, source.id(), 50);
        peer.sendWindow(destination.id(), 300);
        byte[] rest = peer.awaitData(destination.id(), 300);
        assertEquals(300, rest.length);
        peer.sendState(source.id(), PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        await(forward);
        peer.expectState(destination.id(), PipeWire.READY | PipeWire.CLIENT_WRITER_COMPLETE, 0);
        assertFalse(source.isOutputClosed(), "the source is left as is");
    }

    @Test
    void bridgeRelaysBothWays() throws Exception {
        connect(SMALL);
        PipeImpl a = openPipe(1000);
        PipeImpl b = registeredPipe(methodResult());
        peer.sendWindow(b.id(), 1000);
        CompletableFuture<Void> bridge = NexusDuplexPipe.bridge(a, b);
        peer.sendData(a.id(), bytes(100, 1));
        peer.sendData(b.id(), bytes(200, 2));
        assertArrayEquals(bytes(100, 1), peer.awaitData(b.id(), 100));
        assertArrayEquals(bytes(200, 2), peer.awaitData(a.id(), 200));
        peer.sendState(a.id(), PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        peer.sendState(b.id(), PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        await(bridge);
    }

    @Test
    void countersReturnToZero() throws Exception {
        int before = PipeCounters.openPipes();
        connect(SMALL);
        PipeImpl pipe = registeredPipe(methodResult());
        peer.sendData(pipe.id(), new byte[100]);
        waitFor(() -> PipeCounters.bufferedBytes() >= 100);
        assertEquals(before + 1, PipeCounters.openPipes());
        session.abort(DisconnectReason.GRACEFUL);
        waitFor(() -> PipeCounters.openPipes() == before);
        assertEquals(0, PipeCounters.bufferedBytes());
    }

    private static byte[] toArray(ByteBuffer buffer) {
        byte[] out = new byte[buffer.remaining()];
        buffer.get(out);
        return out;
    }
}
