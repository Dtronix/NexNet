package com.dtronix.nexnet.serialization;

import com.dtronix.nexnet.NexusException;

/**
 * MessagePack input is malformed, of an unexpected type or over a configured limit, or a value cannot be written.
 * {@link #kind()} names the reason, with the error kinds of the golden vector format ({@code tests/golden/README.md}).
 */
public class MsgPackException extends NexusException {
    private static final long serialVersionUID = 1L;

    /** Why input was refused or a value could not be written. */
    public enum Kind {
        /** The input ends inside the value. */
        TRUNCATED,
        /** A MessagePack type the expected type cannot be read from (or written as). */
        TYPE,
        /** The never-used code {@code 0xc1}. */
        RESERVED,
        /** An integer outside the target type. */
        RANGE,
        /** An array with the wrong number of elements for a fixed shape. */
        COUNT,
        /** A wrong byte length for a fixed-size bin or ext 78 data. */
        SIZE,
        /** A declared length or count larger than the remaining input. */
        LENGTH,
        /** More array elements and map entries than {@link MsgPackOptions#maxElements()}. */
        ELEMENTS,
        /** More nesting than {@link MsgPackOptions#maxDepth()}. */
        DEPTH,
        /** Invalid UTF-8 with {@link MsgPackOptions#strictUtf8()}. */
        UTF8,
        /** A well-formed but invalid value (a date out of range, a decimal scale above 28, ...). */
        VALUE,
        /** Bytes after the value. */
        TRAILING
    }

    private final Kind kind;

    /**
     * Creates the exception with the kind {@link Kind#VALUE}.
     *
     * @param message the detail message
     */
    public MsgPackException(String message) {
        this(Kind.VALUE, message);
    }

    /**
     * Creates the exception with the kind {@link Kind#VALUE}.
     *
     * @param message the detail message
     * @param cause the cause, or null
     */
    public MsgPackException(String message, Throwable cause) {
        this(Kind.VALUE, message, cause);
    }

    /**
     * Creates the exception.
     *
     * @param kind the kind of error
     * @param message the detail message
     */
    public MsgPackException(Kind kind, String message) {
        super(message);
        this.kind = kind;
    }

    /**
     * Creates the exception.
     *
     * @param kind the kind of error
     * @param message the detail message
     * @param cause the cause, or null
     */
    public MsgPackException(Kind kind, String message, Throwable cause) {
        super(message, cause);
        this.kind = kind;
    }

    /**
     * The kind of error.
     *
     * @return the kind
     */
    public Kind kind() {
        return kind;
    }
}
