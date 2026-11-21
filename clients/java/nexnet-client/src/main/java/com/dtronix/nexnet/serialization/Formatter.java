package com.dtronix.nexnet.serialization;

import org.jspecify.annotations.Nullable;

/**
 * Writes and reads one type in MessagePack. Generated code exposes a {@code public static final Formatter<X>} per
 * type (K03); the runtime provides the built-in formatters (K02). Formatters are stateless and thread-safe.
 *
 * <p>A formatter of a nullable type writes nil for null. A formatter of a C# struct type throws on null, on write
 * and on read.
 *
 * @param <T> the Java type
 */
public interface Formatter<T> {
    /**
     * Writes a value.
     *
     * @param writer the writer
     * @param value the value
     * @throws MsgPackException when the value cannot be represented (for example, out of range)
     */
    void write(MsgPackWriter writer, @Nullable T value);

    /**
     * Reads a value.
     *
     * @param reader the reader
     * @return the value
     * @throws MsgPackException when the input is malformed or of the wrong type
     */
    @Nullable T read(MsgPackReader reader);
}
