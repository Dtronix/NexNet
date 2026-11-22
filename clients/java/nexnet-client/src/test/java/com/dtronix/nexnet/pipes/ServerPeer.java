package com.dtronix.nexnet.pipes;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.MessageTypes;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

/**
 * The scripted server side of a pipe test. A background thread reads every frame the client sends; tests take the
 * ones they expect by type, in order per type, since window updates (control sender) and pipe frames (send lane)
 * interleave freely.
 */
final class ServerPeer implements AutoCloseable {
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

        /** A decoded DuplexPipeUpdateState. */
        PipeWire.State state() {
            return PipeWire.parseState(body);
        }

        long window() {
            return PipeWire.parseWindow(body);
        }
    }

    private final FakeServer server;
    private final List<Msg> received = new ArrayList<>();
    private final Thread thread;
    private volatile int disconnect = -1;

    ServerPeer(FakeServer server) {
        this.server = server;
        this.thread = new Thread(this::run, "test-server-reader");
        thread.setDaemon(true);
        thread.start();
    }

    private void run() {
        try {
            while (true) {
                int type = server.readByte();
                if (type < 0) {
                    return;
                }
                if (MessageTypes.isDisconnect(type)) {
                    disconnect = type;
                    synchronized (received) {
                        received.notifyAll();
                    }
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
                if (type == MessageTypes.INVOCATION_CREDIT) {
                    continue;
                }
                synchronized (received) {
                    received.add(msg);
                    received.notifyAll();
                }
            }
        } catch (RuntimeException e) {
            // The transport closed.
        }
    }

    /** The disconnect code the client sent, or -1. */
    int disconnectCode() {
        return disconnect;
    }

    /** Waits for the client's disconnect byte. */
    int awaitDisconnect() throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        synchronized (received) {
            while (disconnect < 0 && System.nanoTime() < deadline) {
                received.wait(50);
            }
        }
        return disconnect;
    }

    /** Takes the first received message matching, waiting up to 5 seconds. */
    Msg take(Predicate<Msg> match, String what) throws InterruptedException {
        Msg msg = poll(match, 5000);
        if (msg == null) {
            throw new AssertionError("No " + what + " received.");
        }
        return msg;
    }

    /** Takes the first message matching within the time, or null. */
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

    Msg take(int type, int pipeId) throws InterruptedException {
        return take(m -> m.type == type && (m.type == MessageTypes.DUPLEX_PIPE_UPDATE_STATE
            ? m.state().pipeId == pipeId : m.postHeader == pipeId), "message of type " + type + " for P" + pipeId);
    }

    /** Takes the next state message of a pipe and checks its state and window. */
    void expectState(int pipeId, int state, long window) throws InterruptedException {
        PipeWire.State s = take(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, pipeId).state();
        assertEquals(state, s.state, "state of P" + pipeId);
        assertEquals(window, s.window, "window of P" + pipeId);
    }

    /** Checks that nothing of a type arrives for a while. */
    void expectNone(int type, long millis) throws InterruptedException {
        assertNull(poll(m -> m.type == type, millis), "unexpected message of type " + type);
    }

    /** Checks that nothing of a type arrives for a pipe for a while. */
    void expectNone(int type, int pipeId, long millis) throws InterruptedException {
        assertNull(poll(m -> m.type == type && m.postHeader == pipeId, millis),
            "unexpected message of type " + type + " for P" + pipeId);
    }

    /** Total credit granted to the server for a pipe in the window updates received so far (taken). */
    long takeGrants(int pipeId) {
        long total = 0;
        synchronized (received) {
            for (Iterator<Msg> it = received.iterator(); it.hasNext(); ) {
                Msg msg = it.next();
                if (msg.type == MessageTypes.PIPE_WINDOW_UPDATE && msg.postHeader == pipeId) {
                    total += msg.window();
                    it.remove();
                }
            }
        }
        return total;
    }

    /** Takes all data received so far for a pipe. */
    byte[] takeData(int pipeId) {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        synchronized (received) {
            for (Iterator<Msg> it = received.iterator(); it.hasNext(); ) {
                Msg msg = it.next();
                if (msg.type == MessageTypes.DUPLEX_PIPE_WRITE && msg.postHeader == pipeId) {
                    out.write(msg.body, 0, msg.body.length);
                    it.remove();
                }
            }
        }
        return out.toByteArray();
    }

    /** Waits until data for a pipe arrives and takes all of it. */
    byte[] awaitData(int pipeId, int atLeast) throws InterruptedException {
        java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (out.size() < atLeast) {
            byte[] data = takeData(pipeId);
            out.write(data, 0, data.length);
            if (out.size() >= atLeast) {
                break;
            }
            if (System.nanoTime() > deadline) {
                throw new AssertionError("Only " + out.size() + " of " + atLeast + " bytes arrived on P" + pipeId);
            }
            synchronized (received) {
                received.wait(20);
            }
        }
        return out.toByteArray();
    }

    // ------------------------------------------------------------------ sending

    void sendState(int pipeId, int state, long window) {
        server.write(Frame.of(MessageTypes.DUPLEX_PIPE_UPDATE_STATE, PipeWire.stateBody(pipeId, state, window))
            .bytes());
    }

    void sendData(int pipeId, byte[] data) {
        sendData(pipeId, data, 0, data.length);
    }

    void sendData(int pipeId, byte[] data, int offset, int length) {
        server.write(Frame.of(MessageTypes.DUPLEX_PIPE_WRITE, pipeId, data, offset, length).bytes());
    }

    void sendWindow(int pipeId, long grant) {
        server.write(Frame.of(MessageTypes.PIPE_WINDOW_UPDATE, pipeId, PipeWire.windowBody(grant), 0, 4).bytes());
    }

    void send(int type, byte[] body) {
        server.write(Frame.of(type, body).bytes());
    }

    @Override
    public void close() {
        thread.interrupt();
    }
}
