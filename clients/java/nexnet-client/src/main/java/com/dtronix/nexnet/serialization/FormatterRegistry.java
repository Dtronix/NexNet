package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.NexusDateOnly;
import com.dtronix.nexnet.NexusDateTime;
import com.dtronix.nexnet.NexusDateTimeOffset;
import com.dtronix.nexnet.NexusDecimal;
import com.dtronix.nexnet.NexusTimeOnly;
import com.dtronix.nexnet.NexusTimeSpan;
import com.dtronix.nexnet.NexusVersion;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import org.jspecify.annotations.Nullable;

/**
 * Formatters looked up by Java class, for values whose type is only known at run time (the Java side of
 * {@code [assembly: NexusSerializable<T>]}). Generated code passes formatters explicitly and does not need this.
 * Thread-safe.
 *
 * <p>A Java class can stand for several C# types ({@code Integer} is C# {@code int}, {@code byte} and
 * {@code ushort}); the built-in registry maps each boxed class to its signed C# type of the same width
 * ({@code Byte} to {@code sbyte}, {@code Short} to {@code short}, {@code Integer} to {@code int}, {@code Long} to
 * {@code long}, {@code Float} to {@code float}), {@code byte[]} to bin and {@code String} to {@code string}.
 */
public final class FormatterRegistry {
    private static final FormatterRegistry BUILT_IN = createBuiltIn();

    private final ConcurrentHashMap<Class<?>, Formatter<?>> formatters = new ConcurrentHashMap<>();
    private final boolean readOnly;

    /** Creates an empty registry. */
    public FormatterRegistry() {
        this(false);
    }

    private FormatterRegistry(boolean readOnly) {
        this.readOnly = readOnly;
    }

    private static FormatterRegistry createBuiltIn() {
        FormatterRegistry r = new FormatterRegistry(false);
        r.put(Boolean.class, Formatters.BOOLEAN);
        r.put(Byte.class, Formatters.SBYTE);
        r.put(Short.class, Formatters.SHORT);
        r.put(Integer.class, Formatters.INT);
        r.put(Long.class, Formatters.LONG);
        r.put(Float.class, Formatters.FLOAT);
        r.put(Double.class, Formatters.DOUBLE);
        r.put(Character.class, Formatters.CHAR);
        r.put(String.class, Formatters.STRING);
        r.put(byte[].class, Formatters.BINARY);
        r.put(short[].class, Formatters.SHORT_ARRAY);
        r.put(int[].class, Formatters.INT_ARRAY);
        r.put(long[].class, Formatters.LONG_ARRAY);
        r.put(float[].class, Formatters.FLOAT_ARRAY);
        r.put(double[].class, Formatters.DOUBLE_ARRAY);
        r.put(char[].class, Formatters.CHAR_ARRAY);
        r.put(BigDecimal.class, Formatters.DECIMAL);
        r.put(NexusDecimal.class, Formatters.NEXUS_DECIMAL);
        r.put(BigInteger.class, Formatters.BIG_INTEGER);
        r.put(UUID.class, Formatters.GUID);
        r.put(NexusDateTime.class, Formatters.DATE_TIME);
        r.put(NexusDateTimeOffset.class, Formatters.DATE_TIME_OFFSET);
        r.put(NexusTimeSpan.class, Formatters.TIME_SPAN);
        r.put(NexusDateOnly.class, Formatters.DATE_ONLY);
        r.put(NexusTimeOnly.class, Formatters.TIME_ONLY);
        r.put(NexusVersion.class, Formatters.VERSION);
        FormatterRegistry frozen = new FormatterRegistry(true);
        frozen.formatters.putAll(r.formatters);
        return frozen;
    }

    private <T> void put(Class<T> type, Formatter<T> formatter) {
        formatters.put(type, formatter);
    }

    /**
     * The registry that holds the runtime's built-in formatters. It is read-only; to add formatters, create a
     * registry and {@link #registerAll(FormatterRegistry) copy} the built-ins into it.
     *
     * @return the shared registry
     */
    public static FormatterRegistry builtIn() {
        return BUILT_IN;
    }

    /**
     * Registers a formatter, replacing any formatter registered for the same class.
     *
     * @param type the class
     * @param formatter the formatter
     * @param <T> the type
     * @throws UnsupportedOperationException on the {@link #builtIn()} registry
     */
    public <T> void register(Class<T> type, Formatter<T> formatter) {
        if (readOnly) {
            throw new UnsupportedOperationException("The built-in registry is read-only.");
        }
        if (type == null || formatter == null) {
            throw new NullPointerException(type == null ? "type" : "formatter");
        }
        formatters.put(type, formatter);
    }

    /**
     * Registers every formatter of another registry, replacing formatters registered for the same classes.
     *
     * @param other the registry to copy
     * @throws UnsupportedOperationException on the {@link #builtIn()} registry
     */
    public void registerAll(FormatterRegistry other) {
        if (readOnly) {
            throw new UnsupportedOperationException("The built-in registry is read-only.");
        }
        formatters.putAll(other.formatters);
    }

    /**
     * Finds the formatter registered for a class.
     *
     * @param type the class
     * @param <T> the type
     * @return the formatter, or null when none is registered
     */
    @SuppressWarnings("unchecked")
    public <T> @Nullable Formatter<T> find(Class<T> type) {
        return (Formatter<T>) formatters.get(type);
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
        Formatter<T> formatter = find(type);
        if (formatter == null) {
            throw new MsgPackException(MsgPackException.Kind.TYPE, "No formatter is registered for " + type.getName()
                + ".");
        }
        return formatter;
    }
}
