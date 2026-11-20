/**
 * The replica of one enable run of a synchronized list (protocol spec §12.4–§12.6, C# `NexusListClient.OnProcess`):
 * the reset sequence with its change buffer, the stale rule, validation, apply, and the answer to the single pending
 * request. It does no IO; {@link NexusList} feeds it the items read from the collection pipe. Each enable gets a fresh
 * replica, so nothing of a closed pipe can reach the next one.
 * @module
 */
export type NexusListChange<T> = {
    action: 'add' | 'remove' | 'replace' | 'move' | 'reset' | 'ready';
    /**
     * add, remove, replace: the index of the item.
     */
    index?: number;
    /**
     * add: the new item; remove: the removed item; replace: the new item; move: the moved item.
     */
    item?: T;
    /**
     * replace: the item that was replaced.
     */
    oldItem?: T;
    /**
     * move.
     */
    fromIndex?: number;
    /**
     * move.
     */
    toIndex?: number;
};
export type ReplicaHooks<T> = {
    /**
     * Raises a change event.
     */
    emit: (change: NexusListChange<T>) => void;
    /**
     * The first reset of the run completed (after its `reset` and `ready`).
     */
    synchronized: () => void;
};
/**
 * @template T
 */
export declare class ListReplica<T> {
    #private;
    /** The items, in list order. Replaced, never mutated, so a snapshot taken by a reader stays valid. @type {readonly T[]} */
    items: readonly T[];
    /** The list version (§12.3). */
    version: number;
    /** True once the first reset of this run has completed. */
    synchronized: boolean;
    /** @param {ReplicaHooks<T>} hooks */
    constructor(hooks: ReplicaHooks<T>);
    /** True while a request waits for its answer. */
    get hasPending(): boolean;
    /**
     * Registers the single request in flight. The promise resolves with true when its Ack change is applied, and with
     * false on a Noop, an Ack change that fails validation, or a close.
     * @returns {Promise<boolean>}
     */
    beginRequest(): Promise<boolean>;
    /**
     * Answers the pending request, if any.
     * @param {boolean} applied
     */
    completePending(applied: boolean): void;
    /**
     * The pipe closed (C# `OnDisconnected`): the list empties, the version returns to 0, a partial reset is dropped and
     * the pending request completes with false. Raises nothing; the caller raises `reset`.
     */
    close(): void;
    /**
     * Processes one item read from the pipe.
     * @param {import('./messages.js').ListMessage<T>} message
     * @returns {string | null} A protocol error (the caller closes the pipe), or null.
     */
    process(message: import('./messages.js').ListMessage<T>): string | null;
}
