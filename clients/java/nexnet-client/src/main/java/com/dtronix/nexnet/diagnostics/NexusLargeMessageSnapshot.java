package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * An argument or result larger than one frame in transfer. Mirrors the C#
 * {@code NexusLargeMessageSnapshot}.
 */
public final class NexusLargeMessageSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "direction", "kind", "invocationId", "length", "transferred", "sinceLastProgress", "aborting"));

    private final NexusDirection direction;
    private final NexusLargeMessageKind kind;
    private final int invocationId;
    private final long length;
    private final long transferred;
    private final NexusTimeSpan sinceLastProgress;
    private final boolean aborting;

    /**
     * Creates a snapshot.
     *
     * @param direction whether it is received or sent
     * @param kind argument or result
     * @param invocationId the ID of the invocation it belongs to
     * @param length the total length, in bytes
     * @param transferred the bytes transferred so far
     * @param sinceLastProgress the time since the last chunk
     * @param aborting true for a refused inbound value whose remaining chunks are discarded
     */
    public NexusLargeMessageSnapshot(NexusDirection direction, NexusLargeMessageKind kind, int invocationId, long length, long transferred, NexusTimeSpan sinceLastProgress, boolean aborting) {
        this.direction = Objects.requireNonNull(direction, "direction");
        this.kind = Objects.requireNonNull(kind, "kind");
        this.invocationId = invocationId;
        this.length = length;
        this.transferred = transferred;
        this.sinceLastProgress = Objects.requireNonNull(sinceLastProgress, "sinceLastProgress");
        this.aborting = aborting;
    }

    /**
     * Whether it is received or sent.
     *
     * @return the value
     */
    public NexusDirection direction() {
        return direction;
    }

    /**
     * Argument or result.
     *
     * @return the value
     */
    public NexusLargeMessageKind kind() {
        return kind;
    }

    /**
     * The ID of the invocation it belongs to.
     *
     * @return the value
     */
    public int invocationId() {
        return invocationId;
    }

    /**
     * The total length, in bytes.
     *
     * @return the value
     */
    public long length() {
        return length;
    }

    /**
     * The bytes transferred so far.
     *
     * @return the value
     */
    public long transferred() {
        return transferred;
    }

    /**
     * The time since the last chunk.
     *
     * @return the value
     */
    public NexusTimeSpan sinceLastProgress() {
        return sinceLastProgress;
    }

    /**
     * True for a refused inbound value whose remaining chunks are discarded.
     *
     * @return the value
     */
    public boolean aborting() {
        return aborting;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusLargeMessageSnapshot)) {
            return false;
        }
        NexusLargeMessageSnapshot o = (NexusLargeMessageSnapshot) obj;
        return Objects.equals(direction, o.direction)
            && Objects.equals(kind, o.kind)
            && invocationId == o.invocationId
            && length == o.length
            && transferred == o.transferred
            && Objects.equals(sinceLastProgress, o.sinceLastProgress)
            && aborting == o.aborting;
    }

    @Override
    public int hashCode() {
        return Objects.hash(direction, kind, invocationId, length, transferred, sinceLastProgress, aborting);
    }

    @Override
    public String toString() {
        return "NexusLargeMessageSnapshot[" + "direction=" + direction
            + ", kind=" + kind
            + ", invocationId=" + invocationId
            + ", length=" + length
            + ", transferred=" + transferred
            + ", sinceLastProgress=" + sinceLastProgress
            + ", aborting=" + aborting + "]";
    }
}
