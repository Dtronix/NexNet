package com.dtronix.nexnet.transport;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ProtocolException;
import java.security.SecureRandom;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;

/**
 * RFC 6455 client framing over a connected byte stream. NexNet uses WebSocket as a byte stream: outgoing bytes go in
 * masked binary frames, and the payloads of incoming binary messages (fragmented or not) are concatenated. Pings are
 * answered with pongs, and the close handshake is answered and ends the input. No subprotocol and no extensions.
 */
final class WebSocketStreams {
    static final int OP_CONTINUATION = 0x0;
    static final int OP_TEXT = 0x1;
    static final int OP_BINARY = 0x2;
    static final int OP_CLOSE = 0x8;
    static final int OP_PING = 0x9;
    static final int OP_PONG = 0xA;

    /** Normal closure. */
    static final int CLOSE_NORMAL = 1000;
    /** Protocol error. */
    static final int CLOSE_PROTOCOL_ERROR = 1002;
    /** Unsupported data (a text message). */
    static final int CLOSE_UNSUPPORTED = 1003;

    /** Outgoing frames carry at most this many payload bytes. */
    static final int MAX_OUTGOING_PAYLOAD = 16 * 1024;

    private final Input input;
    private final Output output;

    WebSocketStreams(InputStream raw, OutputStream rawOut, SecureRandom random) {
        this.output = new Output(rawOut, random);
        this.input = new Input(raw, output);
    }

    InputStream input() {
        return input;
    }

    OutputStream output() {
        return output;
    }

    /**
     * Sends a close frame if the writer is not mid-frame, waiting a short time at most. Used when the transport
     * closes; the socket closes right after, so the server's reply is not awaited.
     */
    void closeQuietly(int code) {
        output.tryClose(code);
    }

    /** Reads frames; data payloads are the stream's bytes. Used only by the session's reader thread. */
    static final class Input extends InputStream {
        private final InputStream raw;
        private final Output output;
        private long remaining;
        private boolean inMessage;
        private boolean ended;

        Input(InputStream raw, Output output) {
            this.raw = raw;
            this.output = output;
        }

        @Override
        public int read() throws IOException {
            byte[] one = new byte[1];
            int n = read(one, 0, 1);
            return n < 0 ? -1 : one[0] & 0xFF;
        }

        @Override
        public int read(byte[] b, int off, int len) throws IOException {
            if (len == 0) {
                return 0;
            }
            while (remaining == 0) {
                if (ended || !nextFrame()) {
                    ended = true;
                    return -1;
                }
            }
            int n = raw.read(b, off, (int) Math.min(len, remaining));
            if (n < 0) {
                throw new EOFException("The connection closed inside a WebSocket frame.");
            }
            remaining -= n;
            return n;
        }

        @Override
        public int available() throws IOException {
            return (int) Math.min(remaining, raw.available());
        }

        @Override
        public void close() throws IOException {
            raw.close();
        }

        /**
         * Reads one frame header and handles a control frame.
         *
         * @return false at the end of the stream (a close frame, or the connection closing between frames)
         */
        private boolean nextFrame() throws IOException {
            int b0 = raw.read();
            if (b0 < 0) {
                return false;
            }
            int b1 = readByte();
            boolean fin = (b0 & 0x80) != 0;
            int opcode = b0 & 0x0F;
            if ((b0 & 0x70) != 0) {
                throw fail(CLOSE_PROTOCOL_ERROR, "A WebSocket frame sets reserved bits; no extension was negotiated.");
            }
            if ((b1 & 0x80) != 0) {
                throw fail(CLOSE_PROTOCOL_ERROR, "The server sent a masked WebSocket frame.");
            }
            long length = b1 & 0x7F;
            if (length == 126) {
                length = (readByte() << 8) | readByte();
            } else if (length == 127) {
                length = 0;
                for (int i = 0; i < 8; i++) {
                    length = (length << 8) | readByte();
                }
                if (length < 0) {
                    throw fail(CLOSE_PROTOCOL_ERROR, "A WebSocket frame length has the most significant bit set.");
                }
            }
            if (opcode >= 0x8) {
                if (!fin || length > 125) {
                    throw fail(CLOSE_PROTOCOL_ERROR, "A WebSocket control frame is fragmented or too long.");
                }
                byte[] payload = new byte[(int) length];
                readFully(payload);
                switch (opcode) {
                    case OP_CLOSE:
                        // Echo the status code (RFC 6455 5.5.1), then end the stream.
                        int code = payload.length >= 2 ? ((payload[0] & 0xFF) << 8) | (payload[1] & 0xFF)
                            : CLOSE_NORMAL;
                        output.sendClose(code);
                        return false;
                    case OP_PING:
                        output.sendControl(OP_PONG, payload);
                        return true;
                    case OP_PONG:
                        return true;
                    default:
                        throw fail(CLOSE_PROTOCOL_ERROR, "Unknown WebSocket control opcode " + opcode + ".");
                }
            }
            switch (opcode) {
                case OP_CONTINUATION:
                    if (!inMessage) {
                        throw fail(CLOSE_PROTOCOL_ERROR, "A WebSocket continuation frame without a message.");
                    }
                    break;
                case OP_BINARY:
                    if (inMessage) {
                        throw fail(CLOSE_PROTOCOL_ERROR, "A new WebSocket message inside a fragmented one.");
                    }
                    break;
                case OP_TEXT:
                    throw fail(CLOSE_UNSUPPORTED, "The server sent a WebSocket text message; NexNet is binary only.");
                default:
                    throw fail(CLOSE_PROTOCOL_ERROR, "Unknown WebSocket data opcode " + opcode + ".");
            }
            inMessage = !fin;
            remaining = length;
            return true;
        }

        private ProtocolException fail(int code, String message) {
            output.tryClose(code);
            return new ProtocolException(message);
        }

        private int readByte() throws IOException {
            int b = raw.read();
            if (b < 0) {
                throw new EOFException("The connection closed inside a WebSocket frame header.");
            }
            return b;
        }

        private void readFully(byte[] b) throws IOException {
            int off = 0;
            while (off < b.length) {
                int n = raw.read(b, off, b.length - off);
                if (n < 0) {
                    throw new EOFException("The connection closed inside a WebSocket control frame.");
                }
                off += n;
            }
        }
    }

    /**
     * Writes masked frames. Data is buffered by the session's writer thread and framed on flush or when the buffer
     * fills; control frames come from the reader thread or from close. A lock keeps frames whole.
     */
    static final class Output extends OutputStream {
        private final OutputStream raw;
        private final SecureRandom random;
        private final ReentrantLock lock = new ReentrantLock();
        private final byte[] buffer = new byte[MAX_OUTGOING_PAYLOAD];
        private int count;
        private boolean closeSent;

        Output(OutputStream raw, SecureRandom random) {
            this.raw = raw;
            this.random = random;
        }

        @Override
        public void write(int b) throws IOException {
            if (count == buffer.length) {
                drain();
            }
            buffer[count++] = (byte) b;
        }

        @Override
        public void write(byte[] b, int off, int len) throws IOException {
            if ((off | len) < 0 || off + len > b.length) {
                throw new IndexOutOfBoundsException();
            }
            while (len > 0) {
                if (count == 0 && len >= buffer.length) {
                    sendFrame(OP_BINARY, b, off, buffer.length, false);
                    off += buffer.length;
                    len -= buffer.length;
                    continue;
                }
                int n = Math.min(len, buffer.length - count);
                System.arraycopy(b, off, buffer, count, n);
                count += n;
                off += n;
                len -= n;
                if (count == buffer.length) {
                    drain();
                }
            }
        }

        @Override
        public void flush() throws IOException {
            if (count > 0) {
                sendFrame(OP_BINARY, buffer, 0, count, true);
                count = 0;
            } else {
                lock.lock();
                try {
                    raw.flush();
                } finally {
                    lock.unlock();
                }
            }
        }

        @Override
        public void close() throws IOException {
            raw.close();
        }

        private void drain() throws IOException {
            sendFrame(OP_BINARY, buffer, 0, count, false);
            count = 0;
        }

        void sendControl(int opcode, byte[] payload) throws IOException {
            lock.lock();
            try {
                if (!closeSent) {
                    sendFrame(opcode, payload, 0, payload.length, true);
                }
            } finally {
                lock.unlock();
            }
        }

        void sendClose(int code) throws IOException {
            lock.lock();
            try {
                if (closeSent) {
                    return;
                }
                sendFrame(OP_CLOSE, new byte[] {(byte) (code >>> 8), (byte) code}, 0, 2, true);
                closeSent = true;
            } finally {
                lock.unlock();
            }
        }

        /** Sends a close frame unless the lock is held for long (the writer is blocked) or one was sent. */
        void tryClose(int code) {
            try {
                if (!lock.tryLock(100, TimeUnit.MILLISECONDS)) {
                    return;
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return;
            }
            try {
                sendClose(code);
            } catch (IOException ignored) {
                // The connection is going away.
            } finally {
                lock.unlock();
            }
        }

        private void sendFrame(int opcode, byte[] data, int off, int len, boolean flush) throws IOException {
            int headerLength = len < 126 ? 2 : len < 65536 ? 4 : 10;
            byte[] frame = new byte[headerLength + 4 + len];
            frame[0] = (byte) (0x80 | opcode);
            if (len < 126) {
                frame[1] = (byte) (0x80 | len);
            } else if (len < 65536) {
                frame[1] = (byte) (0x80 | 126);
                frame[2] = (byte) (len >>> 8);
                frame[3] = (byte) len;
            } else {
                frame[1] = (byte) (0x80 | 127);
                long l = len;
                for (int i = 0; i < 8; i++) {
                    frame[2 + i] = (byte) (l >>> (56 - 8 * i));
                }
            }
            byte[] mask = new byte[4];
            random.nextBytes(mask);
            System.arraycopy(mask, 0, frame, headerLength, 4);
            int p = headerLength + 4;
            for (int i = 0; i < len; i++) {
                frame[p + i] = (byte) (data[off + i] ^ mask[i & 3]);
            }
            lock.lock();
            try {
                if (closeSent) {
                    throw new IOException("The WebSocket close frame was already sent.");
                }
                raw.write(frame);
                if (flush) {
                    raw.flush();
                }
            } finally {
                lock.unlock();
            }
        }
    }
}
