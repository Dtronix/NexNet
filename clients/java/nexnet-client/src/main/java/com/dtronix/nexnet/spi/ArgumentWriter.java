package com.dtronix.nexnet.spi;

import com.dtronix.nexnet.serialization.MsgPackWriter;

/**
 * Writes the serialized arguments of an invocation. Generated proxies pass a lambda that writes a MessagePack
 * array header of the method's serialized argument count, then each argument with its formatter, in wire order.
 */
@FunctionalInterface
public interface ArgumentWriter {
    /** For a method with no serialized arguments: the invocation carries an empty argument payload. */
    ArgumentWriter NONE = writer -> {
    };

    /**
     * Writes the arguments.
     *
     * @param writer the writer
     */
    void write(MsgPackWriter writer);
}
