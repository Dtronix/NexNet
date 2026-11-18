package com.dtronix.nexnet.pipes;

import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.util.concurrent.CompletableFuture;
import org.jspecify.annotations.Nullable;

/**
 * A bidirectional byte stream multiplexed over the session. Create one with
 * {@code NexusClient.createPipe()} and pass it to a generated proxy method that takes a pipe, or receive one as the
 * argument of a client nexus method.
 *
 * <p>Credit is granted to the peer only as this side reads, so a slow reader applies backpressure end to end.
 * Implemented by K08.
 */
public interface NexusDuplexPipe extends AutoCloseable {
    /**
     * The pipe id on the wire, or 0 while unassigned.
     *
     * @return the id
     */
    int id();

    /**
     * Completes when both sides have set up the pipe and data can flow.
     *
     * @return the future
     */
    CompletableFuture<Void> ready();

    /**
     * Completes when both directions are complete or the pipe is reset.
     *
     * @return the future
     */
    CompletableFuture<Void> completed();

    /**
     * Reads the next chunk of input.
     *
     * @return a future of the next chunk, or of null when the peer completed its output
     */
    CompletableFuture<@Nullable ByteBuffer> read();

    /**
     * Writes bytes to the peer. The future completes once the bytes are within the peer's credit and queued for
     * sending.
     *
     * @param data the bytes; read from its position to its limit
     * @return the future
     */
    CompletableFuture<Void> write(ByteBuffer data);

    /**
     * Completes this side's output. The peer reads end of input after the remaining bytes.
     *
     * @return a future that completes when the completion is sent
     */
    CompletableFuture<Void> completeOutput();

    /**
     * A blocking {@link InputStream} over {@link #read()}. Never use it on the Android main thread.
     *
     * @return the stream
     */
    InputStream inputStream();

    /**
     * A blocking {@link OutputStream} over {@link #write(ByteBuffer)}. Never use it on the Android main thread.
     *
     * @return the stream
     */
    OutputStream outputStream();

    /** Completes the output and releases the pipe. */
    @Override
    void close();
}
