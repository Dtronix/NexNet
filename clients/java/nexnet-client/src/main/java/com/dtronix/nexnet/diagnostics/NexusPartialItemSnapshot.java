package com.dtronix.nexnet.diagnostics;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * A channel item that has partly arrived. Mirrors the C# {@code NexusPartialItemSnapshot}.
 */
public final class NexusPartialItemSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "length", "received"));

    private final long length;
    private final long received;

    /**
     * Creates a snapshot.
     *
     * @param length the length of the item, length prefix included
     * @param received the bytes of it received so far
     */
    public NexusPartialItemSnapshot(long length, long received) {
        this.length = length;
        this.received = received;
    }

    /**
     * The length of the item, length prefix included.
     *
     * @return the value
     */
    public long length() {
        return length;
    }

    /**
     * The bytes of it received so far.
     *
     * @return the value
     */
    public long received() {
        return received;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusPartialItemSnapshot)) {
            return false;
        }
        NexusPartialItemSnapshot o = (NexusPartialItemSnapshot) obj;
        return length == o.length
            && received == o.received;
    }

    @Override
    public int hashCode() {
        return Objects.hash(length, received);
    }

    @Override
    public String toString() {
        return "NexusPartialItemSnapshot[" + "length=" + length
            + ", received=" + received + "]";
    }
}
