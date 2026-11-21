package com.dtronix.nexnet.serialization;

/**
 * Hardening options for reading MessagePack, the same as the C# {@code NexusSerializerOptions}. Length checks before
 * allocation and non-recursive skipping are always on. Immutable.
 */
public final class MsgPackOptions {
    /** The default maximum depth for data from untrusted peers. */
    public static final int DEFAULT_MAX_DEPTH = 64;

    /** Data from an untrusted peer: depth limit 64, strict UTF-8, no element limit. The default. */
    public static final MsgPackOptions UNTRUSTED = new MsgPackOptions(false, DEFAULT_MAX_DEPTH, true, 0);

    /** Data from a trusted peer: no depth limit, lenient UTF-8, no element limit. */
    public static final MsgPackOptions TRUSTED = new MsgPackOptions(true, Integer.MAX_VALUE, false, 0);

    private final boolean trusted;
    private final int maxDepth;
    private final boolean strictUtf8;
    private final int maxElements;

    private MsgPackOptions(boolean trusted, int maxDepth, boolean strictUtf8, int maxElements) {
        if (maxDepth < 0) {
            throw new IllegalArgumentException("maxDepth must not be negative");
        }
        if (maxElements < 0) {
            throw new IllegalArgumentException("maxElements must not be negative");
        }
        this.trusted = trusted;
        this.maxDepth = maxDepth;
        this.strictUtf8 = strictUtf8;
        this.maxElements = maxElements;
    }

    /**
     * True for the trusted security level.
     *
     * @return whether the data is trusted
     */
    public boolean trusted() {
        return trusted;
    }

    /**
     * The most nesting of arrays and maps of sequences, sets, maps, tuples, objects and unions in one value.
     * {@code maxDepth} levels read, one more fails.
     *
     * @return the maximum depth
     */
    public int maxDepth() {
        return maxDepth;
    }

    /**
     * True when invalid UTF-8 in a string fails; false replaces each maximal invalid subpart with U+FFFD.
     *
     * @return whether UTF-8 is strict
     */
    public boolean strictUtf8() {
        return strictUtf8;
    }

    /**
     * The most array elements plus map keys and values one value may contain, counted over the whole value; 0 means
     * no limit.
     *
     * @return the maximum element count
     */
    public int maxElements() {
        return maxElements;
    }

    /**
     * A copy with another maximum depth.
     *
     * @param value the maximum depth (0 or more)
     * @return the options
     */
    public MsgPackOptions withMaxDepth(int value) {
        return new MsgPackOptions(trusted, value, strictUtf8, maxElements);
    }

    /**
     * A copy with strict or lenient UTF-8.
     *
     * @param value true for strict UTF-8
     * @return the options
     */
    public MsgPackOptions withStrictUtf8(boolean value) {
        return new MsgPackOptions(trusted, maxDepth, value, maxElements);
    }

    /**
     * A copy with another element limit.
     *
     * @param value the maximum element count, 0 for no limit
     * @return the options
     */
    public MsgPackOptions withMaxElements(int value) {
        return new MsgPackOptions(trusted, maxDepth, strictUtf8, value);
    }

    @Override
    public String toString() {
        return "MsgPackOptions[trusted=" + trusted + ", maxDepth=" + maxDepth + ", strictUtf8=" + strictUtf8
            + ", maxElements=" + maxElements + "]";
    }
}
