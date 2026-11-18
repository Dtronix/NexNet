package com.dtronix.nexnet.collections;

import org.jspecify.annotations.Nullable;

/**
 * One change to a {@link NexusList}, delivered to change listeners on the callback executor.
 *
 * @param <T> the item type
 */
public final class NexusListChange<T> {
    /** What changed, as in the C# {@code NexusCollectionChangedAction}. */
    public enum Action {
        /** An item was added or inserted. */
        ADD,
        /** An item was removed. */
        REMOVE,
        /** An item was replaced. */
        REPLACE,
        /** An item was moved. */
        MOVE,
        /** The contents changed wholesale (reset or clear). */
        RESET,
        /** The list is synchronized and ready for use. */
        READY
    }

    private final Action action;
    private final int index;
    private final int oldIndex;
    private final @Nullable T item;
    private final @Nullable T oldItem;

    /**
     * Creates a change.
     *
     * @param action what changed
     * @param index the index of the item after the change, or -1
     * @param oldIndex the index before the change (move, remove), or -1
     * @param item the new item, or null
     * @param oldItem the old item (replace, remove), or null
     */
    public NexusListChange(Action action, int index, int oldIndex, @Nullable T item, @Nullable T oldItem) {
        if (action == null) {
            throw new NullPointerException("action");
        }
        this.action = action;
        this.index = index;
        this.oldIndex = oldIndex;
        this.item = item;
        this.oldItem = oldItem;
    }

    /**
     * What changed.
     *
     * @return the action
     */
    public Action action() {
        return action;
    }

    /**
     * The index of the item after the change.
     *
     * @return the index, or -1
     */
    public int index() {
        return index;
    }

    /**
     * The index of the item before the change.
     *
     * @return the index, or -1
     */
    public int oldIndex() {
        return oldIndex;
    }

    /**
     * The new item.
     *
     * @return the item, or null
     */
    public @Nullable T item() {
        return item;
    }

    /**
     * The old item.
     *
     * @return the item, or null
     */
    public @Nullable T oldItem() {
        return oldItem;
    }

    @Override
    public String toString() {
        return "NexusListChange[" + action + ", index=" + index + ", oldIndex=" + oldIndex + "]";
    }
}
