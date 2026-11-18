package com.dtronix.nexnet.serialization;

import org.jspecify.annotations.Nullable;

/**
 * Formatters looked up by Java class, for values whose type is only known at run time (the Java side of
 * {@code [assembly: NexusSerializable<T>]}). Generated code passes formatters explicitly and does not need this.
 * Thread-safe.
 *
 * <p>Skeleton (K01); implemented by K02.
 */
public final class FormatterRegistry {
    /** Creates an empty registry. */
    public FormatterRegistry() {
    }

    /**
     * The registry that holds the runtime's built-in formatters.
     *
     * @return the shared registry
     */
    public static FormatterRegistry builtIn() {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Registers a formatter, replacing any formatter registered for the same class.
     *
     * @param type the class
     * @param formatter the formatter
     * @param <T> the type
     */
    public <T> void register(Class<T> type, Formatter<T> formatter) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Finds the formatter registered for a class.
     *
     * @param type the class
     * @param <T> the type
     * @return the formatter, or null when none is registered
     */
    public <T> @Nullable Formatter<T> find(Class<T> type) {
        throw new UnsupportedOperationException("not implemented");
    }

    /**
     * Gets the formatter registered for a class.
     *
     * @param type the class
     * @param <T> the type
     * @return the formatter
     * @throws MsgPackException when none is registered
     */
    public <T> Formatter<T> get(Class<T> type) {
        throw new UnsupportedOperationException("not implemented");
    }
}
