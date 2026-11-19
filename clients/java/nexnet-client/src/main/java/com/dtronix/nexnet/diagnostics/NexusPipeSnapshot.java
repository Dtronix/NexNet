package com.dtronix.nexnet.diagnostics;

import com.dtronix.nexnet.NexusTimeSpan;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Objects;
import org.jspecify.annotations.Nullable;

/**
 * One pipe. Mirrors the C# {@code NexusPipeSnapshot}.
 */
public final class NexusPipeSnapshot {
    /** The field names, in the order of the C# record (camelCase); checked against tests/golden/snapshot-fields.json. */
    public static final List<String> FIELDS = Collections.unmodifiableList(Arrays.asList(
        "id", "isReady", "readerCompleted", "writerCompleted", "closing", "window", "receiveCredit",
        "buffered", "consumedNotGranted", "extraOutstanding", "sinceLastData", "partialItem", "sendCredit",
        "waitingForCredit", "waitingBytes"));

    private final int id;
    private final boolean isReady;
    private final boolean readerCompleted;
    private final boolean writerCompleted;
    private final @Nullable NexusTimeSpan closing;
    private final int window;
    private final long receiveCredit;
    private final long buffered;
    private final long consumedNotGranted;
    private final long extraOutstanding;
    private final @Nullable NexusTimeSpan sinceLastData;
    private final @Nullable NexusPartialItemSnapshot partialItem;
    private final long sendCredit;
    private final @Nullable NexusTimeSpan waitingForCredit;
    private final long waitingBytes;

    /**
     * Creates a snapshot.
     *
     * @param id the pipe ID; until the peer acknowledges a pipe this side opened, only this side's half is set
     * @param isReady true once both sides set the pipe up
     * @param readerCompleted true once this side's reader completed
     * @param writerCompleted true once this side's writer completed
     * @param closing how long ago this side sent its Complete without confirmation, or null when not closing
     * @param window the receive window, in bytes
     * @param receiveCredit the bytes the peer may still send
     * @param buffered the bytes received and not yet read
     * @param consumedNotGranted the bytes read and not yet granted back
     * @param extraOutstanding the extra credit granted for an item larger than the window, not yet consumed
     * @param sinceLastData the time since data last arrived, or null when none has
     * @param partialItem the channel item that has partly arrived, or null
     * @param sendCredit the bytes this side may still send
     * @param waitingForCredit how long the writer has waited for credit, or null when not waiting
     * @param waitingBytes the bytes the writer still has to send while it waits; 0 when not waiting
     */
    public NexusPipeSnapshot(int id, boolean isReady, boolean readerCompleted, boolean writerCompleted, @Nullable NexusTimeSpan closing, int window, long receiveCredit, long buffered, long consumedNotGranted, long extraOutstanding, @Nullable NexusTimeSpan sinceLastData, @Nullable NexusPartialItemSnapshot partialItem, long sendCredit, @Nullable NexusTimeSpan waitingForCredit, long waitingBytes) {
        this.id = id;
        this.isReady = isReady;
        this.readerCompleted = readerCompleted;
        this.writerCompleted = writerCompleted;
        this.closing = closing;
        this.window = window;
        this.receiveCredit = receiveCredit;
        this.buffered = buffered;
        this.consumedNotGranted = consumedNotGranted;
        this.extraOutstanding = extraOutstanding;
        this.sinceLastData = sinceLastData;
        this.partialItem = partialItem;
        this.sendCredit = sendCredit;
        this.waitingForCredit = waitingForCredit;
        this.waitingBytes = waitingBytes;
    }

    /**
     * The pipe ID; until the peer acknowledges a pipe this side opened, only this side's half is set.
     *
     * @return the value
     */
    public int id() {
        return id;
    }

    /**
     * True once both sides set the pipe up.
     *
     * @return the value
     */
    public boolean isReady() {
        return isReady;
    }

    /**
     * True once this side's reader completed.
     *
     * @return the value
     */
    public boolean readerCompleted() {
        return readerCompleted;
    }

    /**
     * True once this side's writer completed.
     *
     * @return the value
     */
    public boolean writerCompleted() {
        return writerCompleted;
    }

    /**
     * How long ago this side sent its Complete without confirmation, or null when not closing.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan closing() {
        return closing;
    }

    /**
     * The receive window, in bytes.
     *
     * @return the value
     */
    public int window() {
        return window;
    }

    /**
     * The bytes the peer may still send.
     *
     * @return the value
     */
    public long receiveCredit() {
        return receiveCredit;
    }

    /**
     * The bytes received and not yet read.
     *
     * @return the value
     */
    public long buffered() {
        return buffered;
    }

    /**
     * The bytes read and not yet granted back.
     *
     * @return the value
     */
    public long consumedNotGranted() {
        return consumedNotGranted;
    }

    /**
     * The extra credit granted for an item larger than the window, not yet consumed.
     *
     * @return the value
     */
    public long extraOutstanding() {
        return extraOutstanding;
    }

    /**
     * The time since data last arrived, or null when none has.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan sinceLastData() {
        return sinceLastData;
    }

    /**
     * The channel item that has partly arrived, or null.
     *
     * @return the value
     */
    public @Nullable NexusPartialItemSnapshot partialItem() {
        return partialItem;
    }

    /**
     * The bytes this side may still send.
     *
     * @return the value
     */
    public long sendCredit() {
        return sendCredit;
    }

    /**
     * How long the writer has waited for credit, or null when not waiting.
     *
     * @return the value
     */
    public @Nullable NexusTimeSpan waitingForCredit() {
        return waitingForCredit;
    }

    /**
     * The bytes the writer still has to send while it waits; 0 when not waiting.
     *
     * @return the value
     */
    public long waitingBytes() {
        return waitingBytes;
    }

    @Override
    public boolean equals(@Nullable Object obj) {
        if (this == obj) {
            return true;
        }
        if (!(obj instanceof NexusPipeSnapshot)) {
            return false;
        }
        NexusPipeSnapshot o = (NexusPipeSnapshot) obj;
        return id == o.id
            && isReady == o.isReady
            && readerCompleted == o.readerCompleted
            && writerCompleted == o.writerCompleted
            && Objects.equals(closing, o.closing)
            && window == o.window
            && receiveCredit == o.receiveCredit
            && buffered == o.buffered
            && consumedNotGranted == o.consumedNotGranted
            && extraOutstanding == o.extraOutstanding
            && Objects.equals(sinceLastData, o.sinceLastData)
            && Objects.equals(partialItem, o.partialItem)
            && sendCredit == o.sendCredit
            && Objects.equals(waitingForCredit, o.waitingForCredit)
            && waitingBytes == o.waitingBytes;
    }

    @Override
    public int hashCode() {
        return Objects.hash(id, isReady, readerCompleted, writerCompleted, closing, window, receiveCredit, buffered, consumedNotGranted, extraOutstanding, sinceLastData, partialItem, sendCredit, waitingForCredit, waitingBytes);
    }

    @Override
    public String toString() {
        return "NexusPipeSnapshot[" + "id=" + id
            + ", isReady=" + isReady
            + ", readerCompleted=" + readerCompleted
            + ", writerCompleted=" + writerCompleted
            + ", closing=" + closing
            + ", window=" + window
            + ", receiveCredit=" + receiveCredit
            + ", buffered=" + buffered
            + ", consumedNotGranted=" + consumedNotGranted
            + ", extraOutstanding=" + extraOutstanding
            + ", sinceLastData=" + sinceLastData
            + ", partialItem=" + partialItem
            + ", sendCredit=" + sendCredit
            + ", waitingForCredit=" + waitingForCredit
            + ", waitingBytes=" + waitingBytes + "]";
    }
}
