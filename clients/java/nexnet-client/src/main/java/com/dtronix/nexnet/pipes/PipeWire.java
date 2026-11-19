package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.DisconnectReason;
import com.dtronix.nexnet.session.SessionProtocolException;

/**
 * The wire forms of the pipe messages (protocol specification sections 4.6, 7.2 and 7.4): the
 * {@code DuplexPipeUpdateState} body, the {@code PipeWindowUpdate} body and the channel item length prefix.
 */
final class PipeWire {
    /** The client-to-server direction is complete. */
    static final int CLIENT_WRITER_COMPLETE = 1;
    /** The server-to-client direction is complete. */
    static final int CLIENT_READER_COMPLETE = 2;
    /** The pipe is open. */
    static final int READY = 4;
    /** The open was refused. */
    static final int REFUSED = 8;
    /** Both directions complete and ready: the pipe is closed. */
    static final int COMPLETE = CLIENT_WRITER_COMPLETE | CLIENT_READER_COMPLETE | READY;
    /** The invocation flags of a pipe, channel or collection method: IgnoreReturn | DuplexPipe. */
    static final int INVOCATION_FLAGS = 3;
    /** The most credit a peer may grant on a pipe. */
    static final long MAX_CREDIT = 1L << 31;
    /** The longest channel item prefix. */
    static final int MAX_PREFIX_LENGTH = 5;

    private PipeWire() {
    }

    /** A decoded {@code DuplexPipeUpdateState}. */
    static final class State {
        final int pipeId;
        final int state;
        final long window;

        State(int pipeId, int state, long window) {
            this.pipeId = pipeId;
            this.state = state;
            this.window = window;
        }
    }

    /** {@code [pipeId: uint16, state: uint8, window: uint32]} in the most compact encoding. */
    static byte[] stateBody(int pipeId, int state, long window) {
        byte[] buffer = new byte[16];
        int size = 0;
        buffer[size++] = (byte) 0x93;
        size = writeUnsigned(buffer, size, pipeId);
        size = writeUnsigned(buffer, size, state);
        size = writeUnsigned(buffer, size, window);
        byte[] body = new byte[size];
        System.arraycopy(buffer, 0, body, 0, size);
        return body;
    }

    private static int writeUnsigned(byte[] buffer, int at, long value) {
        int p = at;
        if (value <= 0x7F) {
            buffer[p++] = (byte) value;
        } else if (value <= 0xFF) {
            buffer[p++] = (byte) 0xCC;
            buffer[p++] = (byte) value;
        } else if (value <= 0xFFFF) {
            buffer[p++] = (byte) 0xCD;
            buffer[p++] = (byte) (value >>> 8);
            buffer[p++] = (byte) value;
        } else {
            buffer[p++] = (byte) 0xCE;
            buffer[p++] = (byte) (value >>> 24);
            buffer[p++] = (byte) (value >>> 16);
            buffer[p++] = (byte) (value >>> 8);
            buffer[p++] = (byte) value;
        }
        return p;
    }

    /** Decodes a {@code DuplexPipeUpdateState} body; a malformed body is a protocol error. */
    static State parseState(byte[] body) {
        int[] position = {0};
        if (body.length < 1) {
            throw protocolError("DuplexPipeUpdateState is empty.");
        }
        int header = body[position[0]++] & 0xFF;
        int count;
        if (header >= 0x90 && header <= 0x9F) {
            count = header & 0x0F;
        } else if (header == 0xDC) {
            count = (int) readBigEndian(body, position, 2);
        } else {
            throw protocolError("DuplexPipeUpdateState is not an array.");
        }
        if (count != 3) {
            throw protocolError("DuplexPipeUpdateState has " + count + " elements.");
        }
        long pipeId = readUnsigned(body, position);
        long state = readUnsigned(body, position);
        long window = readUnsigned(body, position);
        if (position[0] != body.length) {
            throw protocolError("Trailing bytes after DuplexPipeUpdateState.");
        }
        if (pipeId > 0xFFFF || state > 0xFF || window > 0xFFFFFFFFL) {
            throw protocolError("DuplexPipeUpdateState value out of range.");
        }
        return new State((int) pipeId, (int) state, window);
    }

    private static long readUnsigned(byte[] body, int[] position) {
        if (position[0] >= body.length) {
            throw protocolError("DuplexPipeUpdateState is truncated.");
        }
        int type = body[position[0]++] & 0xFF;
        long value;
        if (type <= 0x7F) {
            return type;
        }
        switch (type) {
            case 0xCC:
                return readBigEndian(body, position, 1);
            case 0xCD:
                return readBigEndian(body, position, 2);
            case 0xCE:
                return readBigEndian(body, position, 4);
            case 0xCF:
                value = readBigEndian(body, position, 8);
                break;
            case 0xD0:
                value = (byte) readBigEndian(body, position, 1);
                break;
            case 0xD1:
                value = (short) readBigEndian(body, position, 2);
                break;
            case 0xD2:
                value = (int) readBigEndian(body, position, 4);
                break;
            case 0xD3:
                value = readBigEndian(body, position, 8);
                break;
            default:
                throw protocolError("DuplexPipeUpdateState holds a non-integer.");
        }
        if (value < 0) {
            throw protocolError("DuplexPipeUpdateState holds a negative or too large integer.");
        }
        return value;
    }

    private static long readBigEndian(byte[] body, int[] position, int bytes) {
        if (position[0] + bytes > body.length) {
            throw protocolError("DuplexPipeUpdateState is truncated.");
        }
        long value = 0;
        for (int i = 0; i < bytes; i++) {
            value = (value << 8) | (body[position[0]++] & 0xFF);
        }
        return value;
    }

    /** The {@code PipeWindowUpdate} body: a little-endian {@code uint32}. */
    static byte[] windowBody(long grant) {
        return new byte[] {(byte) grant, (byte) (grant >>> 8), (byte) (grant >>> 16), (byte) (grant >>> 24)};
    }

    /** Decodes a {@code PipeWindowUpdate} body. */
    static long parseWindow(byte[] body) {
        if (body.length != 4) {
            throw protocolError("PipeWindowUpdate body is " + body.length + " bytes.");
        }
        return (body[0] & 0xFFL) | ((body[1] & 0xFFL) << 8) | ((body[2] & 0xFFL) << 16) | ((body[3] & 0xFFL) << 24);
    }

    /** Bytes needed to write a channel item length prefix. */
    static int prefixLength(int length) {
        int bytes = 1;
        for (long value = length & 0xFFFFFFFFL; value >= 0x80; value >>>= 7) {
            bytes++;
        }
        return bytes;
    }

    /** Writes a channel item length prefix (unsigned LEB128); returns the position after it. */
    static int writePrefix(byte[] destination, int at, int length) {
        long value = length & 0xFFFFFFFFL;
        int p = at;
        while (value >= 0x80) {
            destination[p++] = (byte) (value | 0x80);
            value >>>= 7;
        }
        destination[p++] = (byte) value;
        return p;
    }

    static SessionProtocolException protocolError(String message) {
        return new SessionProtocolException(DisconnectReason.PROTOCOL_ERROR, message);
    }
}
