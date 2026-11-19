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
 * Futures complete on the client's callback executor when the session's reader thread completes them.
 *
 * <p>A pipe passed to a server method is released by {@link #close()}; a pipe received as an argument of a client
 * nexus method is closed by the runtime when the method's future completes.
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
     * Reads the next chunk of input. Reading is what grants the peer more credit. Concurrent reads are served in
     * call order.
     *
     * @return a future of the next chunk, or of null when the peer completed its output (or the pipe or session
     *     ended)
     */
    CompletableFuture<@Nullable ByteBuffer> read();

    /**
     * Writes bytes to the peer. The future completes once the bytes are within the peer's credit and queued for
     * sending.
     *
     * @param data the bytes; read from its position to its limit (copied, so the buffer may be reused at once)
     * @return the future; fails with an {@link IllegalStateException} once the output is complete or the peer
     *     completed its input, and with a {@link com.dtronix.nexnet.session.SessionClosedException} when the session
     *     ended
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

    /**
     * Copies everything this pipe receives to {@code destination} until this pipe's input completes, then completes
     * the destination's output (half-close). This pipe is left as is. Each buffer is read only after the previous
     * one is within the destination's credit, so a slow destination stalls this pipe instead of buffering.
     *
     * @param destination the pipe to write to
     * @return a future that completes when this pipe's input completed or the destination's output was completed
     */
    default CompletableFuture<Void> forwardTo(NexusDuplexPipe destination) {
        return PipeForwarding.forward(this, destination);
    }

    /**
     * Forwards {@code a} to {@code b} and {@code b} to {@code a} at once. Each direction half-closes its destination
     * when its source completes. If a direction fails, both outputs are completed and the future fails.
     *
     * @param a the first pipe
     * @param b the second pipe
     * @return a future that completes when both directions have ended
     */
    static CompletableFuture<Void> bridge(NexusDuplexPipe a, NexusDuplexPipe b) {
        return PipeForwarding.bridge(a, b);
    }

    /**
     * Completes the output, discards unread input and releases the pipe once the peer has closed it too. Closing a
     * pipe that was never passed to a method frees it at once.
     */
    @Override
    void close();
}
