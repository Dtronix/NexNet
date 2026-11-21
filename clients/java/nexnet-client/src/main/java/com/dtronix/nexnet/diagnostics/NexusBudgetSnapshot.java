package com.dtronix.nexnet.diagnostics;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * A session's receive memory. Mirrors the C# {@code NexusBudgetSnapshot}.
 */
public final class NexusBudgetSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "limit", "reserved"));

    private final long limit;
    private final long reserved;

    /**
     * Creates a snapshot.
     *
     * @param limit the budget in total, in bytes
     * @param reserved the bytes reserved: pipe windows, the invocation queue and large values
     */
    public NexusBudgetSnapshot(long limit, long reserved) {
        this.limit = limit;
        this.reserved = reserved;
    }

    /**
     * The budget in total, in bytes.
     *
     * @return the value
     */
    public long limit() {
        return limit;
    }

    /**
     * The bytes reserved: pipe windows, the invocation queue and large values.
     *
     * @return the value
     */
    public long reserved() {
        return reserved;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusBudgetSnapshot)) {
            return false;
        }
        NexusBudgetSnapshot o = (NexusBudgetSnapshot) obj;
        return limit == o.limit
            && reserved == o.reserved;
    }

    @Override
    public int hashCode() {
        return Objects.hash(limit, reserved);
    }

    @Override
    public String toString() {
        return "NexusBudgetSnapshot[" + "limit=" + limit
            + ", reserved=" + reserved + "]";
    }
}
