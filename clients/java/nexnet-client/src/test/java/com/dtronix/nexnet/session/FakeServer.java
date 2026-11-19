package com.dtronix.nexnet.session;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.UncheckedIOException;

/** The server side of a {@link LoopbackTransport}: reads what the client wrote, writes what the client reads. */
public final class FakeServer {
    /** The preamble both sides send. */
    public static final byte[] PREAMBLE = {0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1};

    private final InputStream in;
    private final OutputStream out;
    private final LoopbackTransport transport;

    FakeServer(InputStream in, OutputStream out, LoopbackTransport transport) {
        this.in = in;
        this.out = out;
        this.transport = transport;
    }

    /** Writes raw bytes to the client. */
    public void write(byte[] bytes) {
        try {
            out.write(bytes);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    /** Writes the preamble and a ServerGreeting. */
    public void accept(int clientNexusHash, long clientId) {
        write(PREAMBLE);
        write(serverGreeting(clientNexusHash, clientId, 4 * 1024 * 1024, 32));
    }

    /** Ends the stream to the client (the client reads end of stream). */
    public void endStream() {
        transport.closeFromServer();
    }

    /** Reads exactly n bytes from the client. */
    public byte[] read(int n) {
        byte[] bytes = new byte[n];
        int offset = 0;
        try {
            while (offset < n) {
                int r = in.read(bytes, offset, n - offset);
                if (r < 0) {
                    throw new EOFException("End of stream after " + offset + " of " + n + " bytes.");
                }
                offset += r;
            }
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
        return bytes;
    }

    /** Reads one byte, or -1 at end of stream. */
    public int readByte() {
        try {
            byte[] one = new byte[1];
            int r = in.read(one, 0, 1);
            return r < 0 ? -1 : one[0] & 0xFF;
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    /** Reads the client's preamble and checks it. */
    public void expectPreamble() {
        assertArrayEquals(PREAMBLE, read(8));
    }

    /** Reads one framed message with a body (type, length LE, body) and returns all its bytes. */
    public byte[] readFrame() {
        byte[] header = read(3);
        int length = (header[1] & 0xFF) | ((header[2] & 0xFF) << 8);
        byte[] body = read(length);
        byte[] frame = new byte[3 + length];
        System.arraycopy(header, 0, frame, 0, 3);
        System.arraycopy(body, 0, frame, 3, length);
        return frame;
    }

    /** Reads the preamble and the greeting and returns the greeting frame. */
    public byte[] readHandshake() {
        expectPreamble();
        return readFrame();
    }

    /** Expects end of stream next. */
    public void expectEnd() {
        assertEquals(-1, readByte());
    }

    /** A ServerGreeting frame. */
    public static byte[] serverGreeting(int version, long clientId, int maxMessageSize, int credits) {
        WireWriter w = new WireWriter(32);
        w.writeArrayHeader(4);
        w.writeInt(version);
        w.writeInt(clientId);
        w.writeInt(maxMessageSize);
        w.writeInt(credits);
        return Frame.of(MessageTypes.SERVER_GREETING, w.toByteArray()).bytes();
    }

    /** A ServerGreeting frame with the S04 idle timeout and extra trailing elements. */
    public static byte[] serverGreetingWithIdle(int version, long clientId, long idleTimeoutMillis, int extra) {
        WireWriter w = new WireWriter(32);
        w.writeArrayHeader(5 + extra);
        w.writeInt(version);
        w.writeInt(clientId);
        w.writeInt(4 * 1024 * 1024);
        w.writeInt(32);
        w.writeInt(idleTimeoutMillis);
        for (int i = 0; i < extra; i++) {
            w.writeString("future");
        }
        return Frame.of(MessageTypes.SERVER_GREETING, w.toByteArray()).bytes();
    }
}
