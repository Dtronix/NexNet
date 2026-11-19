package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import java.util.Arrays;

/**
 * A payload codec for tests that moves raw MessagePack bytes, so the invocation runtime can be tested without the
 * MessagePack codec (K02). Argument writers and formatters exchange bytes through thread locals on the thread the
 * runtime calls them on.
 */
final class TestCodec implements PayloadCodec {
    static final TestCodec INSTANCE = new TestCodec();
    private static final ThreadLocal<byte[]> OUT = new ThreadLocal<>();
    private static final ThreadLocal<byte[]> IN = new ThreadLocal<>();

    /** A formatter of raw MessagePack values. */
    static final Formatter<byte[]> BYTES = new Formatter<byte[]>() {
        @Override
        public void write(MsgPackWriter writer, byte[] value) {
            OUT.set(value);
        }

        @Override
        public byte[] read(MsgPackReader reader) {
            return IN.get();
        }
    };

    /** Arguments given as raw MessagePack (the array header included). */
    static ArgumentWriter args(byte[] raw) {
        return writer -> OUT.set(raw);
    }

    /** The serialized arguments of the invocation being dispatched on this thread. */
    static byte[] currentArguments() {
        return IN.get();
    }

    @Override
    public byte[] writeArguments(ArgumentWriter arguments) {
        OUT.remove();
        arguments.write(null);
        byte[] bytes = OUT.get();
        OUT.remove();
        return bytes == null ? new byte[0] : bytes;
    }

    @Override
    public byte[] writePipeArguments(PipeArgumentWriter arguments, int initialPipeId) {
        OUT.remove();
        arguments.write(null, initialPipeId);
        byte[] bytes = OUT.get();
        OUT.remove();
        return bytes == null ? new byte[0] : bytes;
    }

    @Override
    public <T> byte[] writeValue(Formatter<T> formatter, T value) {
        OUT.remove();
        formatter.write(null, value);
        byte[] bytes = OUT.get();
        OUT.remove();
        if (bytes == null) {
            throw new IllegalStateException("The formatter wrote nothing.");
        }
        return bytes;
    }

    @Override
    public <T> T readValue(Formatter<T> formatter, byte[] buffer, int offset, int length) {
        IN.set(Arrays.copyOfRange(buffer, offset, offset + length));
        try {
            return formatter.read(null);
        } finally {
            IN.remove();
        }
    }

    @Override
    public MsgPackReader reader(byte[] buffer, int offset, int length) {
        // Read by the test nexus through currentArguments(), on this thread.
        IN.set(Arrays.copyOfRange(buffer, offset, offset + length));
        return null;
    }
}
