package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.serialization.MsgPackWriter;

/**
 * Writes the serialized arguments of a pipe or channel invocation. The pipe or channel argument is written at its
 * wire position as the initial pipe id the runtime assigns ({@code writer.writeInt(initialPipeId)}).
 */
@FunctionalInterface
public interface PipeArgumentWriter {
    /**
     * Writes the arguments.
     *
     * @param writer the writer
     * @param initialPipeId the pipe id to write in place of the pipe or channel argument (1–255)
     */
    void write(MsgPackWriter writer, int initialPipeId);
}
