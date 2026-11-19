package com.dtronix.nexnet.invocation;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.dtronix.nexnet.session.FakeServer;
import com.dtronix.nexnet.session.Frame;
import com.dtronix.nexnet.session.MessageTypes;
import java.io.ByteArrayOutputStream;

/** The scripted server side of a test: reads and writes framed messages over a loopback transport. */
final class Peer {
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
    }

    private final FakeServer server;
    /** Invocation credit the client returned, summed over the InvocationCredit messages skipped by {@link #next}. */
    int creditsReturned;

    Peer(FakeServer server) {
        this.server = server;
    }

    FakeServer server() {
        return server;
    }

    /** Reads the next message, of any type. */
    Msg read() {
        int type = server.readByte();
        if (type < 0) {
            throw new IllegalStateException("End of stream.");
        }
        if (type == MessageTypes.PING || MessageTypes.isDisconnect(type)) {
            return new Msg(type, 0, new byte[0]);
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
        return new Msg(type, post, server.read(bodyLength));
    }

    /** Reads the next message other than InvocationCredit, adding returned credit to {@link #creditsReturned}. */
    Msg next() {
        while (true) {
            Msg msg = read();
            if (msg.type != MessageTypes.INVOCATION_CREDIT) {
                return msg;
            }
            WireIn r = new WireIn(msg.body, 0, msg.body.length);
            r.arrayHeader();
            creditsReturned += r.readUnsigned(0xFFFF);
        }
    }

    /** Reads the next message other than InvocationCredit and checks its type. */
    Msg expect(int type) {
        Msg msg = next();
        assertEquals(type, msg.type, "message type");
        return msg;
    }

    /** Reads messages until the returned credit reaches {@code total}. */
    void awaitCredits(int total) {
        while (creditsReturned < total) {
            Msg msg = read();
            assertEquals(MessageTypes.INVOCATION_CREDIT, msg.type, "message type");
            WireIn r = new WireIn(msg.body, 0, msg.body.length);
            r.arrayHeader();
            creditsReturned += r.readUnsigned(0xFFFF);
        }
    }

    void send(int type, byte[] body) {
        server.write(Frame.of(type, body).bytes());
    }

    void sendChunk(int id, int kind, byte[] payload, int offset, int length) {
        server.write(Frame.of(MessageTypes.MESSAGE_CHUNK, id | (kind << 16), payload, offset, length).bytes());
    }

    /** Streams a body as MessageStart and chunks of at most 65535 bytes. */
    void stream(int id, int kind, int innerType, byte[] body) {
        send(MessageTypes.MESSAGE_START, array(id, kind, innerType, body.length));
        for (int offset = 0; offset < body.length; offset += Frame.MAX_BODY_LENGTH) {
            sendChunk(id, kind, body, offset, Math.min(Frame.MAX_BODY_LENGTH, body.length - offset));
        }
    }

    /** Reads a MessageStart and its chunks and returns the reassembled body. */
    byte[] readStream(int id, int kind, int innerType) {
        Msg start = expect(MessageTypes.MESSAGE_START);
        WireIn r = new WireIn(start.body, 0, start.body.length);
        assertEquals(4, r.arrayHeader());
        assertEquals(id, r.readUnsigned(0xFFFF), "stream id");
        assertEquals(kind, r.readUnsigned(0xFF), "kind");
        assertEquals(innerType, r.readUnsigned(0xFF), "inner type");
        long total = r.readUInt32();
        ByteArrayOutputStream body = new ByteArrayOutputStream();
        while (body.size() < total) {
            Msg chunk = expect(MessageTypes.MESSAGE_CHUNK);
            assertEquals(id | (kind << 16), chunk.postHeader, "chunk post-header");
            body.write(chunk.body, 0, chunk.body.length);
        }
        assertEquals(total, body.size());
        return body.toByteArray();
    }

    /**
     * A MessagePack array of the values: Integer and Long as integers, String as str, null as nil, byte[] embedded
     * as they are.
     */
    static byte[] array(Object... values) {
        WireOut w = new WireOut(32);
        w.arrayHeader(values.length);
        for (Object value : values) {
            if (value == null) {
                w.nil();
            } else if (value instanceof Integer) {
                w.integer((Integer) value);
            } else if (value instanceof Long) {
                w.integer((Long) value);
            } else if (value instanceof String) {
                w.string((String) value);
            } else if (value instanceof byte[]) {
                byte[] raw = (byte[]) value;
                w.raw(raw, 0, raw.length);
            } else {
                throw new IllegalArgumentException(String.valueOf(value));
            }
        }
        return w.toByteArray();
    }

    /** A MessagePack bin32 value of {@code length} patterned bytes. */
    static byte[] bin(int length) {
        byte[] value = new byte[5 + length];
        value[0] = (byte) 0xC6;
        value[1] = (byte) (length >>> 24);
        value[2] = (byte) (length >>> 16);
        value[3] = (byte) (length >>> 8);
        value[4] = (byte) length;
        for (int i = 0; i < length; i++) {
            value[5 + i] = (byte) (i * 31 + 7);
        }
        return value;
    }
}
