package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.NexusMessageTooLargeException;
import com.dtronix.nexnet.invocation.InvocationOptions;
import com.dtronix.nexnet.serialization.MsgPackException;
import com.dtronix.nexnet.session.MessageTypes;
import com.dtronix.nexnet.session.SessionOptions;
import com.dtronix.nexnet.session.SessionTimers;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Optional;
import java.util.Random;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

@Timeout(60)
class ChannelTest extends PipeTestBase {
    static final int WINDOW = 1024;

    /** The server opens a channel to client method 1 with its id {@code serverId}. */
    private NexusDuplexChannel<byte[]> registeredChannel(int serverId) throws Exception {
        CompletableFuture<NexusDuplexChannel<byte[]>> opened = new CompletableFuture<>();
        nexus.methods.put(1, (args, ctx) -> {
            opened.complete(ctx.channel(args[1], TestCodec.BYTES));
            return ctx.result(new CompletableFuture<>());
        });
        invokePipeMethod(1, serverId);
        NexusDuplexChannel<byte[]> channel = await(opened);
        peer.take(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, channel.pipe().id());
        return channel;
    }

    @Test
    void itemsSplitAcrossFramesAreReadInOrder() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        byte[] a = bin(3, 1);
        byte[] b = bin(200, 2);
        byte[] c = bin(0, 3);
        byte[] stream = framed(a, b, c);
        int id = channel.pipe().id();
        peer.sendData(id, stream, 0, 1);
        peer.sendData(id, stream, 1, 8);
        CompletableFuture<Optional<byte[]>> first = reader.read();
        assertArrayEquals(a, await(first).get());
        CompletableFuture<List<byte[]>> batch = reader.readBatch(5);
        Thread.sleep(50);
        assertFalse(batch.isDone(), "the second item is incomplete");
        peer.sendData(id, stream, 9, stream.length - 9);
        List<byte[]> items = await(batch);
        assertEquals(2, items.size());
        assertArrayEquals(b, items.get(0));
        assertArrayEquals(c, items.get(1));
        peer.sendState(id, PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        assertFalse(await(reader.read()).isPresent());
    }

    @Test
    void writeAllSendsTheItemsInOneFrame() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        int id = channel.pipe().id();
        peer.sendWindow(id, 10_000);
        NexusChannelWriter<byte[]> writer = await(channel.writer());
        byte[] a = bin(10, 1);
        byte[] b = bin(300, 2);
        await(writer.writeAll(Arrays.asList(a, b, a)));
        ServerPeer.Msg msg = peer.take(MessageTypes.DUPLEX_PIPE_WRITE, id);
        assertArrayEquals(framed(a, b, a), msg.body);
        await(writer.writeAndComplete(Arrays.asList(b)));
        assertArrayEquals(framed(b), peer.take(MessageTypes.DUPLEX_PIPE_WRITE, id).body);
        peer.expectState(id, PipeWire.READY | PipeWire.CLIENT_WRITER_COMPLETE, 0);
    }

    @Test
    void anItemTooLargeForTheServerFailsWithoutSendingAnything() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        int id = channel.pipe().id();
        peer.sendWindow(id, 10_000);
        NexusChannelWriter<byte[]> writer = await(channel.writer());
        Throwable error = failure(writer.writeAll(Arrays.asList(bin(1, 1), bin(4 * 1024 * 1024, 2))));
        assertInstanceOf(NexusMessageTooLargeException.class, error);
        peer.expectNone(MessageTypes.DUPLEX_PIPE_WRITE, id, 100);
        await(writer.write(bin(2, 2)));
        assertArrayEquals(framed(bin(2, 2)), peer.take(MessageTypes.DUPLEX_PIPE_WRITE, id).body);
    }

    @Test
    void anItemThatIsNotExactlyOneValueResetsThePipe() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        int id = channel.pipe().id();
        // Length 3, but [1] is two bytes followed by a stray one.
        peer.sendData(id, new byte[] {3, (byte) 0x91, 1, 1});
        assertInstanceOf(MsgPackException.class, failure(reader.read()));
        peer.expectState(id, PipeWire.COMPLETE, 0);
        assertFalse(await(reader.read()).isPresent());
        assertTrue(session.isConnected(), "the session stays up");
    }

    @Test
    void aMalformedPrefixResetsThePipe() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        int id = channel.pipe().id();
        byte[] six = {(byte) 0x80, (byte) 0x80, (byte) 0x80, (byte) 0x80, (byte) 0x80, 0};
        peer.sendData(id, six);
        assertInstanceOf(MsgPackException.class, failure(reader.read()));
        peer.expectState(id, PipeWire.COMPLETE, 0);
    }

    @Test
    void anItemLongerThanMaxMessageSizeResetsThePipe() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW), InvocationOptions.defaults(), SessionTimers.system(),
            SessionOptions.defaults().withMaxMessageSize(SessionOptions.MIN_MAX_MESSAGE_SIZE));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        int id = channel.pipe().id();
        byte[] prefix = new byte[5];
        int n = PipeWire.writePrefix(prefix, 0, SessionOptions.MIN_MAX_MESSAGE_SIZE + 1);
        peer.sendData(id, prefix, 0, n);
        assertInstanceOf(NexusMessageTooLargeException.class, failure(reader.read()));
        peer.expectState(id, PipeWire.COMPLETE, 0);
    }

    @Test
    void anItemLongerThanTheWindowGetsExtraCredit() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        int id = channel.pipe().id();
        long budgetBefore = runtime.budget().reserved();
        byte[] item = bin(2995, 4);
        byte[] stream = framed(item);
        assertEquals(3002, stream.length);
        CompletableFuture<Optional<byte[]>> read = reader.read();
        peer.sendData(id, stream, 0, WINDOW);
        // The reader grants the item's size beyond the window from the session budget.
        assertEquals(3002 - WINDOW, peer.take(MessageTypes.PIPE_WINDOW_UPDATE, id).window());
        assertEquals(budgetBefore + 3002 - WINDOW, runtime.budget().reserved());
        peer.sendData(id, stream, WINDOW, stream.length - WINDOW);
        assertArrayEquals(item, await(read).get());
        // Extra credit is paid off first and not granted back; the window part is.
        assertEquals(WINDOW, peer.take(MessageTypes.PIPE_WINDOW_UPDATE, id).window());
        assertEquals(budgetBefore, runtime.budget().reserved());
    }

    @Test
    void readUntilCompleteCollectsEverything() throws Exception {
        connect(PipeOptions.defaults().withPipeWindow(WINDOW));
        NexusDuplexChannel<byte[]> channel = registeredChannel(9);
        NexusChannelReader<byte[]> reader = await(channel.reader());
        int id = channel.pipe().id();
        CompletableFuture<List<byte[]>> all = reader.readUntilComplete();
        for (int i = 0; i < 20; i++) {
            // 310 bytes in all: within the window.
            peer.sendData(id, framed(bin(i, i)));
        }
        peer.sendState(id, PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
        List<byte[]> items = await(all);
        assertEquals(20, items.size());
        for (int i = 0; i < 20; i++) {
            assertArrayEquals(bin(i, i), items.get(i));
        }
    }

    /**
     * Random windows, frame sizes, item sizes and batch sizes in both directions: every item arrives in order and
     * nothing stalls. The same property as the JS test (J08).
     */
    @Test
    void randomizedChannelsPreserveTheStreamWithoutStalling() throws Exception {
        for (int seed = 1; seed <= 25; seed++) {
            runRandomized(seed);
        }
    }

    private void runRandomized(int seed) throws Exception {
        Random random = new Random(seed * 7919L);
        int window = 16 + random.nextInt(4096);
        connect(PipeOptions.defaults().withPipeWindow(window));
        try {
            NexusDuplexChannel<byte[]> channel = registeredChannel(9);
            int id = channel.pipe().id();
            List<byte[]> items = new ArrayList<>();
            int count = 30 + random.nextInt(40);
            for (int i = 0; i < count; i++) {
                int size = random.nextInt(4) == 0 ? random.nextInt(3 * window) : random.nextInt(Math.max(1, window / 4));
                items.add(bin(size, i + seed));
            }

            // Server to client: frames of random size within the credit the client grants.
            NexusChannelReader<byte[]> reader = await(channel.reader());
            List<byte[]> received = new ArrayList<>();
            CompletableFuture<Void> readAll = readRandomBatches(reader, random.nextLong(), received);
            byte[] stream = framed(items.toArray(new byte[0][]));
            long credit = window;
            int position = 0;
            while (position < stream.length) {
                if (credit == 0) {
                    ServerPeer.Msg grant = peer.take(MessageTypes.PIPE_WINDOW_UPDATE, id);
                    credit += grant.window();
                    continue;
                }
                credit += peer.takeGrants(id);
                int n = (int) Math.min(Math.min(1 + random.nextInt(Math.min(65535, 3 * window)), credit),
                    stream.length - position);
                peer.sendData(id, stream, position, n);
                position += n;
                credit -= n;
            }
            peer.sendState(id, PipeWire.READY | PipeWire.CLIENT_READER_COMPLETE, 0);
            readAll.get(20, TimeUnit.SECONDS);
            assertEquals(items.size(), received.size(), "seed " + seed);
            for (int i = 0; i < items.size(); i++) {
                assertArrayEquals(items.get(i), received.get(i), "seed " + seed + " item " + i);
            }

            // Client to server: random groups of writes; the server grants back random amounts of what it got.
            NexusChannelWriter<byte[]> writer = await(channel.writer());
            int serverWindow = 1 + random.nextInt(4096);
            peer.sendWindow(id, serverWindow);
            List<CompletableFuture<Void>> writes = new ArrayList<>();
            for (int i = 0; i < items.size(); ) {
                int group = 1 + random.nextInt(4);
                List<byte[]> part = items.subList(i, Math.min(items.size(), i + group));
                writes.add(part.size() == 1 ? writer.write(part.get(0)) : writer.writeAll(new ArrayList<>(part)));
                i += part.size();
            }
            CompletableFuture<Void> completed = writer.complete();
            ByteArrayOutputStream got = new ByteArrayOutputStream();
            long clientCredit = serverWindow;
            long ungranted = 0;
            while (true) {
                ServerPeer.Msg msg = peer.take(m -> (m.type == MessageTypes.DUPLEX_PIPE_WRITE && m.postHeader == id)
                    || (m.type == MessageTypes.DUPLEX_PIPE_UPDATE_STATE && m.state().pipeId == id), "pipe data");
                if (msg.type == MessageTypes.DUPLEX_PIPE_UPDATE_STATE) {
                    // READY | CLIENT_WRITER_COMPLETE, or COMPLETE: the server's direction ended earlier.
                    assertTrue((msg.state().state & PipeWire.CLIENT_WRITER_COMPLETE) != 0);
                    break;
                }
                assertTrue(msg.body.length <= clientCredit, "the client stays within its credit");
                got.write(msg.body);
                clientCredit -= msg.body.length;
                ungranted += msg.body.length;
                if (clientCredit == 0 || random.nextBoolean()) {
                    long grant = clientCredit == 0 ? ungranted : 1 + (long) random.nextInt((int) ungranted);
                    peer.sendWindow(id, grant);
                    clientCredit += grant;
                    ungranted -= grant;
                }
            }
            for (CompletableFuture<Void> write : writes) {
                write.get(5, TimeUnit.SECONDS);
            }
            completed.get(5, TimeUnit.SECONDS);
            assertArrayEquals(stream, got.toByteArray(), "seed " + seed);
        } finally {
            session.abort(DisconnectReason.GRACEFUL);
            assertTrue(session.awaitThreads());
            waitFor(() -> pipes.openPipes() == 0);
            peer.close();
        }
    }

    private static CompletableFuture<Void> readRandomBatches(NexusChannelReader<byte[]> reader, long seed,
                                                             List<byte[]> received) {
        Random random = new Random(seed);
        return reader.readBatch(1 + random.nextInt(5)).thenCompose(batch -> {
            if (batch.isEmpty()) {
                return CompletableFuture.completedFuture(null);
            }
            received.addAll(batch);
            return readRandomBatches(reader, random.nextLong(), received);
        });
    }
}
