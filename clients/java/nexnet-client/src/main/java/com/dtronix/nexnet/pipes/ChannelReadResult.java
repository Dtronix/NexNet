package com.dtronix.nexnet.pipes;

import org.jspecify.annotations.Nullable;

/**
 * The result of {@link NexusChannelReader#readItem()}: an item, which may be null for a nullable item type, or the
 * end of the channel. Mirrors the JS reader's {@code { value, done }}.
 *
 * @param <T> the item type
 */
public final class ChannelReadResult<T> {
    private static final ChannelReadResult<?> COMPLETED = new ChannelReadResult<>(true, null);

    private final boolean completed;
    private final @Nullable T value;

    private ChannelReadResult(boolean completed, @Nullable T value) {
        this.completed = completed;
        this.value = value;
    }

    /**
     * An item.
     *
     * @param value the item, possibly null
     * @param <T> the item type
     * @return the result
     */
    public static <T> ChannelReadResult<T> of(@Nullable T value) {
        return new ChannelReadResult<>(false, value);
    }

    /**
     * The end of the channel.
     *
     * @param <T> the item type
     * @return the result
     */
    @SuppressWarnings("unchecked")
    public static <T> ChannelReadResult<T> completed() {
        return (ChannelReadResult<T>) COMPLETED;
    }

    /**
     * Whether the channel is complete and every item was read; {@link #value()} is then null.
     *
     * @return true at the end of the channel
     */
    public boolean isCompleted() {
        return completed;
    }

    /**
     * The item read; null at the end of the channel, or when the item itself is null.
     *
     * @return the item
     */
    public @Nullable T value() {
        return value;
    }

    @Override
    public String toString() {
        return completed ? "ChannelReadResult[completed]" : "ChannelReadResult[" + value + "]";
    }
}
