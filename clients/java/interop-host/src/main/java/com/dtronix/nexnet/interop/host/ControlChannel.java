package com.dtronix.nexnet.interop.host;

import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.OutputStream;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Map;

/**
 * The control connection (control protocol §2, §3): frames of a 4-byte big-endian length, a kind byte ({@code 0x01}:
 * UTF-8 JSON object) and the payload. Reads happen on one thread; writes are whole frames under a lock, so frames
 * never interleave.
 */
final class ControlChannel implements AutoCloseable {
    /** The largest frame (64 MiB). */
    static final int MAX_FRAME = 64 * 1024 * 1024;

    private final Socket socket;
    private final DataInputStream in;
    private final OutputStream out;
    private final Object writeLock = new Object();
    private volatile boolean closed;

    ControlChannel(Socket socket) throws IOException {
        this.socket = socket;
        socket.setTcpNoDelay(true);
        this.in = new DataInputStream(new java.io.BufferedInputStream(socket.getInputStream(), 64 * 1024));
        this.out = socket.getOutputStream();
    }

    /**
     * Reads the next message.
     *
     * @return the JSON object, or null at a clean end of the stream
     * @throws IOException on a broken stream or a malformed frame
     */
    Map<String, Object> read() throws IOException {
        int length;
        try {
            length = in.readInt();
        } catch (EOFException e) {
            return null;
        }
        if (length < 1 || length > MAX_FRAME) {
            throw new IOException("Bad control frame length " + Integer.toUnsignedString(length) + ".");
        }
        int kind = in.readUnsignedByte();
        if (kind != 1) {
            throw new IOException("Unsupported control frame kind " + kind + ".");
        }
        byte[] payload = new byte[length - 1];
        in.readFully(payload);
        Object value;
        try {
            value = Json.parse(new String(payload, StandardCharsets.UTF_8));
        } catch (IllegalArgumentException e) {
            throw new IOException("The control frame is not JSON: " + e.getMessage(), e);
        }
        if (!(value instanceof Map)) {
            throw new IOException("The control frame is not a JSON object.");
        }
        @SuppressWarnings("unchecked")
        Map<String, Object> message = (Map<String, Object>) value;
        return message;
    }

    /**
     * Sends one message; a closed connection drops it.
     *
     * @param message the JSON object
     */
    void send(Map<String, Object> message) {
        byte[] payload = Json.write(message).getBytes(StandardCharsets.UTF_8);
        byte[] frame = new byte[payload.length + 5];
        int length = payload.length + 1;
        frame[0] = (byte) (length >>> 24);
        frame[1] = (byte) (length >>> 16);
        frame[2] = (byte) (length >>> 8);
        frame[3] = (byte) length;
        frame[4] = 1;
        System.arraycopy(payload, 0, frame, 5, payload.length);
        synchronized (writeLock) {
            if (closed) {
                return;
            }
            try {
                out.write(frame);
                out.flush();
            } catch (IOException e) {
                closed = true;
            }
        }
    }

    boolean isClosed() {
        return closed;
    }

    @Override
    public void close() {
        closed = true;
        try {
            socket.close();
        } catch (IOException ignored) {
            // Closing anyway.
        }
    }
}
