package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.MessageTypes;
import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

/** The scripted server side of a collection pipe: reads every frame the client sends on a background thread. */
final class CollectionPeer implements AutoCloseable {
    static final int READY = 4;
    static final int COMPLETE = 7;
    static final int REFUSED = 8;

    /** One message read from the client. */
    static final class Msg {
        final int type;
        final int postHeader;
        final byte[] body;

        Msg(int type, int postHeader, byte[] body) {
            this.type = type;
            this.postHeader = postHeader;
            this.body = body;
        }

        /** A DuplexPipeUpdateState body: {pipeId, state, window}. */
        long[] state() {
            CollectionWire.In in = new CollectionWire.In(body);
            in.arrayHeader();
            return new long[] {in.integer(), in.integer(), in.integer()};
        }
    }

    private final FakeServer server;
    private final List<Msg> received = new ArrayList<>();
    private final ByteArrayOutputStream data = new ByteArrayOutputStream();
    private final Thread thread;

    CollectionPeer(FakeServer server) {
        this.server = server;
        this.thread = new Thread(this::run, "test-server-reader");
        thread.setDaemon(true);
        thread.start();
    }

    private void run() {
        try {
            while (true) {
                int type = server.readByte();
                if (type < 0 || MessageTypes.isDisconnect(type)) {
                    return;
                }
                if (type == MessageTypes.PING) {
                    continue;
                }
                byte[] length = server.read(2);
                int bodyLength = (length[0] & 0xFF) | ((length[1] & 0xFF) << 8);
                int postLength = MessageTypes.postHeaderLength(type);
                int post = 0;
                if (postLength > 0) {
                    byte[] p = server.read(postLength);
                    for (int i = 0; i < postLength; i++) {
                        post |= (p[i] & 0xFF) << (8 * i);
                    }
                }
                Msg msg = new Msg(type, post, server.read(bodyLength));
                if (type == MessageTypes.INVOCATION_CREDIT || type == MessageTypes.PIPE_WINDOW_UPDATE) {
                    continue;
                }
                synchronized (received) {
                    if (type == MessageTypes.DUPLEX_PIPE_WRITE) {
                        data.write(msg.body, 0, msg.body.length);
                    } else {
                        received.add(msg);
                    }
                    received.notifyAll();
                }
            }
        } catch (RuntimeException e) {
            // The transport closed.
        }
    }

    /** Takes the first received message matching, waiting up to 5 seconds. */
    Msg take(Predicate<Msg> match, String what) throws InterruptedException {
        Msg msg = poll(match, 5000);
        if (msg == null) {
            throw new AssertionError("No " + what + " received.");
        }
        return msg;
    }

    Msg poll(Predicate<Msg> match, long millis) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(millis);
        synchronized (received) {
            while (true) {
                for (Iterator<Msg> it = received.iterator(); it.hasNext(); ) {
                    Msg msg = it.next();
                    if (match.test(msg)) {
                        it.remove();
                        return msg;
                    }
                }
                long left = deadline - System.nanoTime();
                if (left <= 0) {
                    return null;
                }
                received.wait(Math.max(1, TimeUnit.NANOSECONDS.toMillis(left)));
            }
        }
    }

    Msg take(int type) throws InterruptedException {
        return take(m -> m.type == type, "message of type " + type);
    }

    /** Waits for the client's state message for a pipe and returns the state. */
    int takeState(int pipeId) throws InterruptedException {
        return (int) take(m -> m.type == MessageTypes.DUPLEX_PIPE_UPDATE_STATE && m.state()[0] == pipeId,
            "state for P" + pipeId).state()[1];
    }

    /** Waits for {@code count} channel items written by the client and takes them. */
    List<byte[]> takeItems(int count) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        synchronized (received) {
            while (true) {
                List<byte[]> items = parse(data.toByteArray());
                if (items.size() >= count) {
                    if (items.size() > count) {
                        throw new AssertionError("Expected " + count + " items, got " + items.size());
                    }
                    data.reset();
                    return items;
                }
                long left = deadline - System.nanoTime();
                if (left <= 0) {
                    throw new AssertionError("Only " + items.size() + " of " + count + " items arrived.");
                }
                received.wait(Math.max(1, TimeUnit.NANOSECONDS.toMillis(left)));
            }
        }
    }

    /** Checks that the client writes no item for a while. */
    void expectNoItems(long millis) throws InterruptedException {
        Thread.sleep(millis);
        synchronized (received) {
            if (data.size() != 0) {
                throw new AssertionError("Unexpected items: " + parse(data.toByteArray()).size());
            }
        }
    }

    private static List<byte[]> parse(byte[] bytes) {
        List<byte[]> items = new ArrayList<>();
        int p = 0;
        while (p < bytes.length) {
            long length = 0;
            int shift = 0;
            int b;
            do {
                if (p >= bytes.length) {
                    return items;
                }
                b = bytes[p++] & 0xFF;
                length |= (long) (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            if (p + length > bytes.length) {
                return items;
            }
            byte[] item = new byte[(int) length];
            System.arraycopy(bytes, p, item, 0, (int) length);
            items.add(item);
            p += (int) length;
        }
        return items;
    }

    // ------------------------------------------------------------------ sending

    void sendState(int pipeId, int state, long window) {
        CollectionWire.Out out = new CollectionWire.Out();
        out.arrayHeader(3);
        out.integer(pipeId);
        out.integer(state);
        out.integer(window);
        server.write(Frame.of(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, out.toArray()).bytes());
    }

    /** Sends channel items, each with its length prefix, in one pipe write. */
    void sendItems(int pipeId, byte[]... items) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        for (byte[] item : items) {
            long value = item.length;
            while (value >= 0x80) {
                out.write((int) (value | 0x80));
                value >>>= 7;
            }
            out.write((int) value);
            out.write(item, 0, item.length);
        }
        byte[] bytes = out.toByteArray();
        server.write(Frame.of(MessageTypes.DUPLEX_PIPE_WRITE, pipeId, bytes, 0, bytes.length).bytes());
    }

    @Override
    public void close() {
        thread.interrupt();
    }
}
