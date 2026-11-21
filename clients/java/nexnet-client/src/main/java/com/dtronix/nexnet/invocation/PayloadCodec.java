package com.dtronix.nexnet.invocation;

import com.dtronix.nexnet.serialization.Formatter;
import com.dtronix.nexnet.serialization.MsgPackReader;
import com.dtronix.nexnet.serialization.MsgPackWriter;
import com.dtronix.nexnet.spi.ArgumentWriter;
import com.dtronix.nexnet.spi.PipeArgumentWriter;
import org.jspecify.annotations.Nullable;

/**
 * Turns payloads (arguments and results) into bytes and back. The runtime writes the message envelopes itself and
 * calls this only for what generated code and formatters produce. {@link #MSGPACK} uses
 * {@link MsgPackWriter}/{@link MsgPackReader}; tests substitute their own.
 */
public interface PayloadCodec {
    /** The MessagePack codec. */
    PayloadCodec MSGPACK = new PayloadCodec() {
        @Override
        public byte[] writeArguments(ArgumentWriter arguments) {
            MsgPackWriter writer = new MsgPackWriter();
            arguments.write(writer);
            return writer.toByteArray();
        }

        @Override
        public byte[] writePipeArguments(PipeArgumentWriter arguments, int initialPipeId) {
            MsgPackWriter writer = new MsgPackWriter();
            arguments.write(writer, initialPipeId);
            return writer.toByteArray();
        }

        @Override
        public <T> byte[] writeValue(Formatter<T> formatter, @Nullable T value) {
            MsgPackWriter writer = new MsgPackWriter();
            formatter.write(writer, value);
            return writer.toByteArray();
        }

        @Override
        public <T> @Nullable T readValue(Formatter<T> formatter, byte[] buffer, int offset, int length) {
            return formatter.read(new MsgPackReader(buffer, offset, length));
        }

        @Override
        public MsgPackReader reader(byte[] buffer, int offset, int length) {
            return new MsgPackReader(buffer, offset, length);
        }
    };

    /**
     * Serializes the arguments of an invocation.
     *
     * @param arguments the generated argument writer
     * @return the bytes; empty for no serialized arguments
     */
    byte[] writeArguments(ArgumentWriter arguments);

    /**
     * Serializes the arguments of a pipe or channel invocation.
     *
     * @param arguments the generated argument writer
     * @param initialPipeId the initial pipe id
     * @return the bytes
     */
    byte[] writePipeArguments(PipeArgumentWriter arguments, int initialPipeId);

    /**
     * Serializes a result value.
     *
     * @param formatter the formatter
     * @param value the value
     * @param <T> the type
     * @return the bytes
     */
    <T> byte[] writeValue(Formatter<T> formatter, @Nullable T value);

    /**
     * Reads a result value.
     *
     * @param formatter the formatter
     * @param buffer the buffer
     * @param offset the first byte of the value
     * @param length the bytes of the value
     * @param <T> the type
     * @return the value
     */
    <T> @Nullable T readValue(Formatter<T> formatter, byte[] buffer, int offset, int length);

    /**
     * A reader over the serialized arguments of an incoming invocation, for the generated {@code dispatch}.
     *
     * @param buffer the buffer
     * @param offset the first byte
     * @param length the bytes
     * @return the reader
     */
    MsgPackReader reader(byte[] buffer, int offset, int length);
}
