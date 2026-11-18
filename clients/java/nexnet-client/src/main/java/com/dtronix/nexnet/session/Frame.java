package com.dtronix.nexnet.session;

/**
 * One outbound message, already framed: the type byte, the little-endian body length and post-header when the type
 * has a body, then the body. Immutable once built; the session writes {@link #bytes()} as they are.
 */
public final class Frame {
    /** The longest frame body. */
    public static final int MAX_BODY_LENGTH = 0xFFFF;

    private final int type;
    private final byte[] bytes;

    private Frame(int type, byte[] bytes) {
        this.type = type;
        this.bytes = bytes;
    }

    /**
     * A message without a body: {@code Ping} or a disconnect code.
     *
     * @param type the type
     * @return the frame
     */
    public static Frame headerOnly(int type) {
        if (type != MessageTypes.PING && !MessageTypes.isDisconnect(type)) {
            throw new IllegalArgumentException("Type " + type + " has a body.");
        }
        return new Frame(type, new byte[] {(byte) type});
    }

    /**
     * A message with a body and no post-header.
     *
     * @param type the type
     * @param body the body
     * @return the frame
     */
    public static Frame of(int type, byte[] body) {
        return of(type, 0, body, 0, body.length);
    }

    /**
     * A message with a body. The post-header is written little-endian in {@code postHeaderLength} bytes: the pipe id
     * ({@code clientId | serverId << 8}) for pipe messages, and the invocation id ({@code uint16}) then the kind for
     * {@code MessageChunk} ({@code invocationId | kind << 16}).
     *
     * @param type the type
     * @param postHeader the post-header value, ignored when the type has none
     * @param body the body bytes
     * @param offset the first body byte
     * @param length the body length, at most {@link #MAX_BODY_LENGTH}
     * @return the frame
     */
    public static Frame of(int type, int postHeader, byte[] body, int offset, int length) {
        int post = MessageTypes.postHeaderLength(type);
        if (post < 0) {
            throw new IllegalArgumentException("Type " + type + " has no body.");
        }
        if (length < 0 || length > MAX_BODY_LENGTH) {
            throw new IllegalArgumentException("A frame body is 0 to 65535 bytes: " + length);
        }
        byte[] bytes = new byte[3 + post + length];
        bytes[0] = (byte) type;
        bytes[1] = (byte) length;
        bytes[2] = (byte) (length >>> 8);
        for (int i = 0; i < post; i++) {
            bytes[3 + i] = (byte) (postHeader >>> (8 * i));
        }
        System.arraycopy(body, offset, bytes, 3 + post, length);
        return new Frame(type, bytes);
    }

    /**
     * Wraps bytes that are already framed. For the connection preamble and tests.
     *
     * @param type the type recorded for diagnostics
     * @param bytes the bytes, written as they are
     * @return the frame
     */
    static Frame raw(int type, byte[] bytes) {
        return new Frame(type, bytes);
    }

    /**
     * The message type.
     *
     * @return the type
     */
    public int type() {
        return type;
    }

    /**
     * The framed bytes. Do not modify.
     *
     * @return the bytes
     */
    public byte[] bytes() {
        return bytes;
    }

    /**
     * The framed length.
     *
     * @return the number of bytes
     */
    public int length() {
        return bytes.length;
    }
}
