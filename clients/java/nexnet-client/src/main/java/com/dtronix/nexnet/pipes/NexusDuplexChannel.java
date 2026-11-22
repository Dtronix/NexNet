package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CompletableFuture;

/**
 * A typed channel over a {@link NexusDuplexPipe}. Create one with {@code NexusClient.createChannel(formatter)} and
 * pass it to a generated proxy method that takes a channel, or receive one as the argument of a client nexus method.
 * {@link #over(NexusDuplexPipe, Formatter)} turns an existing pipe into a channel, like the C#
 * {@code INexusDuplexPipe.GetChannel<T>()}.
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

    /**
     * A typed channel over an existing pipe, like the C# {@code INexusDuplexPipe.GetChannel<T>()}: items are written
     * and read as MessagePack values of {@code itemFormatter} on the pipe's bytes. Use it on a pipe of
     * {@code NexusClient.createPipe()} that is passed to a channel method, or on a pipe received as a method argument.
     * Several channels over one pipe share its bytes and its credit; closing any of them closes the pipe.
     *
     * @param pipe a pipe created by the runtime
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel
     * @throws IllegalArgumentException when the pipe was not created by the NexNet runtime
     */
    static <T> NexusDuplexChannel<T> over(NexusDuplexPipe pipe, Formatter<T> itemFormatter) {
        if (pipe == null || itemFormatter == null) {
            throw new NullPointerException("An argument is null.");
        }
        if (!(pipe instanceof PipeImpl)) {
            throw new IllegalArgumentException("The pipe was not created by the NexNet runtime.");
        }
        PipeImpl impl = (PipeImpl) pipe;
        return new DuplexChannelImpl<>(impl, itemFormatter, impl.manager);
    }
}
