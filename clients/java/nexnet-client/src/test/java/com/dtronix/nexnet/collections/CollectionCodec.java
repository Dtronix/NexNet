package com.dtronix.nexnet.collections;

import com.dtronix.nexnet.invocation.PayloadCodec;
import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.PipeArgumentWriter;

/**
 * A payload codec for the collection tests: channel items move as raw bytes ({@link CollectionWire#RAW}), values go
 * through {@link TestFormat}, and the collection invocation's arguments are {@code [pipeId]}.
 */
final class CollectionCodec implements PayloadCodec {
    static final CollectionCodec INSTANCE = new CollectionCodec();

    @Override
    public byte[] writeArguments(ArgumentWriter arguments) {
        throw new UnsupportedOperationException("Collections invoke with a pipe argument only.");
    }

    @Override
    public byte[] writePipeArguments(PipeArgumentWriter arguments, int initialPipeId) {
        if (arguments != CollectionWire.PIPE_ARGUMENT) {
            throw new IllegalArgumentException("Unexpected argument writer.");
        }
        return CollectionWire.pipeArguments(initialPipeId);
    }

    @Override
    @SuppressWarnings("unchecked")
    public <T> byte[] writeValue(Formatter<T> formatter, T value) {
        if (formatter == CollectionWire.RAW) {
            return ((byte[]) value).clone();
        }
        if (formatter instanceof TestFormat) {
            return ((TestFormat<T>) formatter).encode(value);
        }
        throw new IllegalArgumentException("Unexpected formatter " + formatter);
    }

    @Override
    @SuppressWarnings("unchecked")
    public <T> T readValue(Formatter<T> formatter, byte[] buffer, int offset, int length) {
        byte[] bytes = TestFormat.copy(buffer, offset, length);
        if (formatter == CollectionWire.RAW) {
            return (T) bytes;
        }
        if (formatter instanceof TestFormat) {
            return ((TestFormat<T>) formatter).decode(bytes);
        }
        throw new IllegalArgumentException("Unexpected formatter " + formatter);
    }

    @Override
    public MsgPackReader reader(byte[] buffer, int offset, int length) {
        throw new UnsupportedOperationException("The test nexus has no methods.");
    }
}
