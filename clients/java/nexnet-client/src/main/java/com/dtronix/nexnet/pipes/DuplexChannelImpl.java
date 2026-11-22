package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.serialization.Formatter;
import java.util.concurrent.CompletableFuture;

/** A typed channel over a pipe. */
final class DuplexChannelImpl<T> implements NexusDuplexChannel<T> {
    private final PipeImpl pipe;
    private final NexusChannelReader<T> reader;
    private final NexusChannelWriter<T> writer;

    DuplexChannelImpl(PipeImpl pipe, Formatter<T> formatter, PipeManager manager) {
        this.pipe = pipe;
        this.reader = new ChannelReaderImpl<>(pipe, formatter, manager);
        this.writer = new ChannelWriterImpl<>(pipe, formatter, manager);
    }

    @Override
    public NexusDuplexPipe pipe() {
        return pipe;
    }

    @Override
    public CompletableFuture<NexusChannelReader<T>> reader() {
        return pipe.ready().thenApply(v -> reader);
    }

    @Override
    public CompletableFuture<NexusChannelWriter<T>> writer() {
        return pipe.ready().thenApply(v -> writer);
    }

    @Override
    public void close() {
        pipe.close();
    }
}
