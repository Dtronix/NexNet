package com.dtronix.nexnet.session;

/**
 * One received message with a body, after the connection completed, handed to the {@link MessageHandler} on the
 * reader thread. The body array belongs to the handler.
 */
public final class InboundFrame {
    private final int type;
    private final int postHeader;
    private final byte[] body;

    InboundFrame(int type, int postHeader, byte[] body) {
        this.type = type;
        this.postHeader = postHeader;
        this.body = body;
    }

    /**
     * The message type ({@link MessageTypes}).
     *
     * @return the type
     */
    public int type() {
        return type;
    }

    /**
     * The post-header read little-endian: the pipe id ({@code clientId | serverId << 8}) for pipe messages, or
     * {@code invocationId | kind << 16} for {@code MessageChunk}; 0 for other types.
     *
     * @return the post-header value
     */
    public int postHeader() {
        return postHeader;
    }

    /**
     * The pipe id of a {@code DuplexPipeWrite} or {@code PipeWindowUpdate}.
     *
     * @return {@code clientId | serverId << 8}
     */
    public int pipeId() {
        return postHeader & 0xFFFF;
    }

    /**
     * The invocation id of a {@code MessageChunk}.
     *
     * @return the id
     */
    public int chunkInvocationId() {
        return postHeader & 0xFFFF;
    }

    /**
     * The kind of a {@code MessageChunk}.
     *
     * @return the kind
     */
    public int chunkKind() {
        return (postHeader >>> 16) & 0xFF;
    }

    /**
     * The body.
     *
     * @return the body bytes
     */
    public byte[] body() {
        return body;
    }
}
