package com.dtronix.nexnet.pipes;

import com.dtronix.nexnet.serialization.Formatter;

/**
 * Test-only access of the JVM interop host to typed channels over an existing pipe. The control protocol creates a
 * pipe first ({@code pipe.create}) and only later uses it as a channel ({@code channel.writer}, a channel method), like
 * the C# {@code INexusDuplexPipe.GetChannel}; the public runtime API creates a channel together with its pipe. Lives
 * in the runtime's package on the host's class path only (a split package of the unnamed module); never shipped.
 */
public final class InteropChannels {
    private InteropChannels() {
    }

    /**
     * A typed channel over a pipe of {@code NexusClient.createPipe()}.
     *
     * @param pipe the pipe
     * @param itemFormatter the item formatter
     * @param <T> the item type
     * @return the channel view; several views of one pipe share its bytes
     */
    public static <T> NexusDuplexChannel<T> over(NexusDuplexPipe pipe, Formatter<T> itemFormatter) {
        if (!(pipe instanceof PipeImpl)) {
            throw new IllegalArgumentException("The pipe was not created by the NexNet runtime.");
        }
        PipeImpl impl = (PipeImpl) pipe;
        return new DuplexChannelImpl<>(impl, itemFormatter, impl.manager);
    }
}
