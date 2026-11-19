package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * A session's invocations. Mirrors the C# {@code NexusInvocationSnapshot}.
 */
public final class NexusInvocationSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "queued", "executing", "maxExecuting", "inboundCredits", "outboundCredits", "creditWaits",
        "awaitingResults", "creditDrops", "oldestQueuedWait", "separatePoolQueued", "separatePoolExecuting",
        "separatePoolMaxExecuting", "separatePoolOldestQueuedWait"));

    private final int queued;
    private final int executing;
    private final int maxExecuting;
    private final int inboundCredits;
    private final int outboundCredits;
    private final int creditWaits;
    private final int awaitingResults;
    private final long creditDrops;
    private final @Nullable NexusTimeSpan oldestQueuedWait;
    private final int separatePoolQueued;
    private final int separatePoolExecuting;
    private final int separatePoolMaxExecuting;
    private final @Nullable NexusTimeSpan separatePoolOldestQueuedWait;

    /**
     * Creates a snapshot.
     *
     * @param queued the invocations received and waiting for an execution slot
     * @param executing the invocations running
     * @param maxExecuting the most invocations that run at once
     * @param inboundCredits the invocations the peer may still queue on this side
     * @param outboundCredits the invocations this side may still send before the peer returns credit
     * @param creditWaits the calls waiting for the peer to return credit
     * @param awaitingResults the calls this side made that wait for their result
     * @param creditDrops the invocations skipped because the peer returned no credit in time
     * @param oldestQueuedWait how long the oldest queued invocation has waited, or null when none is queued
     * @param separatePoolQueued the separate-pool invocations waiting for a slot
     * @param separatePoolExecuting the separate-pool invocations running
     * @param separatePoolMaxExecuting the most separate-pool invocations that run at once
     * @param separatePoolOldestQueuedWait how long the oldest queued separate-pool invocation has waited, or null when none is queued
     */
    public NexusInvocationSnapshot(int queued, int executing, int maxExecuting, int inboundCredits, int outboundCredits, int creditWaits, int awaitingResults, long creditDrops, @Nullable NexusTimeSpan oldestQueuedWait, int separatePoolQueued, int separatePoolExecuting, int separatePoolMaxExecuting, @Nullable NexusTimeSpan separatePoolOldestQueuedWait) {
        this.queued = queued;
        this.executing = executing;
        this.maxExecuting = maxExecuting;
        this.inboundCredits = inboundCredits;
        this.outboundCredits = outboundCredits;
        this.creditWaits = creditWaits;
        this.awaitingResults = awaitingResults;
        this.creditDrops = creditDrops;
        this.oldestQueuedWait = oldestQueuedWait;
        this.separatePoolQueued = separatePoolQueued;
        this.separatePoolExecuting = separatePoolExecuting;
        this.separatePoolMaxExecuting = separatePoolMaxExecuting;
        this.separatePoolOldestQueuedWait = separatePoolOldestQueuedWait;
    }

    /**
     * The invocations received and waiting for an execution slot.
     *
     * @return the value
     */
    public int queued() {
        return queued;
    }

    /**
     * The invocations running.
     *
     * @return the value
     */
    public int executing() {
        return executing;
    }

    /**
     * The most invocations that run at once.
     *
     * @return the value
     */
    public int maxExecuting() {
        return maxExecuting;
    }

    /**
     * The invocations the peer may still queue on this side.
     *
     * @return the value
     */
    public int inboundCredits() {
        return inboundCredits;
    }

    /**
     * The invocations this side may still send before the peer returns credit.
     *
     * @return the value
     */
    public int outboundCredits() {
        return outboundCredits;
    }

    /**
     * The calls waiting for the peer to return credit.
     *
     * @return the value
     */
    public int creditWaits() {
        return creditWaits;
    }

    /**
     * The calls this side made that wait for their result.
     *
     * @return the value
     */
    public int awaitingResults() {
        return awaitingResults;
    }

    /**
     * The invocations skipped because the peer returned no credit in time.
     *
     * @return the value
     */
    public long creditDrops() {
        return creditDrops;
    }

    /**
     * How long the oldest queued invocation has waited, or null when none is queued.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan oldestQueuedWait() {
        return oldestQueuedWait;
    }

    /**
     * The separate-pool invocations waiting for a slot.
     *
     * @return the value
     */
    public int separatePoolQueued() {
        return separatePoolQueued;
    }

    /**
     * The separate-pool invocations running.
     *
     * @return the value
     */
    public int separatePoolExecuting() {
        return separatePoolExecuting;
    }

    /**
     * The most separate-pool invocations that run at once.
     *
     * @return the value
     */
    public int separatePoolMaxExecuting() {
        return separatePoolMaxExecuting;
    }

    /**
     * How long the oldest queued separate-pool invocation has waited, or null when none is queued.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan separatePoolOldestQueuedWait() {
        return separatePoolOldestQueuedWait;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusInvocationSnapshot)) {
            return false;
        }
        NexusInvocationSnapshot o = (NexusInvocationSnapshot) obj;
        return queued == o.queued
            && executing == o.executing
            && maxExecuting == o.maxExecuting
            && inboundCredits == o.inboundCredits
            && outboundCredits == o.outboundCredits
            && creditWaits == o.creditWaits
            && awaitingResults == o.awaitingResults
            && creditDrops == o.creditDrops
            && Objects.equals(oldestQueuedWait, o.oldestQueuedWait)
            && separatePoolQueued == o.separatePoolQueued
            && separatePoolExecuting == o.separatePoolExecuting
            && separatePoolMaxExecuting == o.separatePoolMaxExecuting
            && Objects.equals(separatePoolOldestQueuedWait, o.separatePoolOldestQueuedWait);
    }

    @Override
    public int hashCode() {
        return Objects.hash(queued, executing, maxExecuting, inboundCredits, outboundCredits, creditWaits, awaitingResults, creditDrops, oldestQueuedWait, separatePoolQueued, separatePoolExecuting, separatePoolMaxExecuting, separatePoolOldestQueuedWait);
    }

    @Override
    public String toString() {
        return "NexusInvocationSnapshot[" + "queued=" + queued
            + ", executing=" + executing
            + ", maxExecuting=" + maxExecuting
            + ", inboundCredits=" + inboundCredits
            + ", outboundCredits=" + outboundCredits
            + ", creditWaits=" + creditWaits
            + ", awaitingResults=" + awaitingResults
            + ", creditDrops=" + creditDrops
            + ", oldestQueuedWait=" + oldestQueuedWait
            + ", separatePoolQueued=" + separatePoolQueued
            + ", separatePoolExecuting=" + separatePoolExecuting
            + ", separatePoolMaxExecuting=" + separatePoolMaxExecuting
            + ", separatePoolOldestQueuedWait=" + separatePoolOldestQueuedWait + "]";
    }
}
