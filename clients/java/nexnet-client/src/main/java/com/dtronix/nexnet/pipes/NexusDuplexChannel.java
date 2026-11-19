package com.dtronix.nexnet.pipes;

import java.util.concurrent.CompletableFuture;

/**
 * A typed channel over a {@link NexusDuplexPipe}. Create one with {@code NexusClient.createChannel(formatter)} and
 * pass it to a generated proxy method that takes a channel, or receive one as the argument of a client nexus method.
 *
 * @param <T> the item type
 */
public interface NexusDuplexChannel<T> extends AutoCloseable {
    /**
     * The underlying pipe.
     *
     * @return the pipe
     */
    NexusDuplexPipe pipe();

    /**
     * The reader, once the pipe is ready.
     *
     * @return a future of the reader
     */
    CompletableFuture<NexusChannelReader<T>> reader();

    /**
     * The writer, once the pipe is ready.
     *
     * @return a future of the writer
     */
    CompletableFuture<NexusChannelWriter<T>> writer();

    /** Completes the output and releases the pipe. */
    @Override
    void close();
}
