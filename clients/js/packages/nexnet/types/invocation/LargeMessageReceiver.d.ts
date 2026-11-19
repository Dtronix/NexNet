/**
 * Reassembles large arguments and results the peer streams to this side (protocol spec §6.2–§6.4, C#
 * `LargeMessageReceiver`). Called from the session's data callback only.
 *
 * A stream is keyed by (invocation ID, kind). Its total length is reserved from the session budget at Start; chunks
 * are copied as they arrive. A stream that can't be reserved is aborted with Busy and its chunks are discarded up to
 * the total length. On completion the reservation passes to the caller with the reassembled body.
 * @module
 */
export type LargeMessageOwner = {
    /**
     * True if this side waits for the call's result.
     */
    isCallOutstanding: (invocationId: number) => boolean;
    /**
     * Takes one inbound credit for a large argument; false when none is left.
     */
    tryTakeInvocationCredit: () => boolean;
    /**
     * Returns a credit taken for an argument that will not be queued.
     */
    returnInvocationCredit: () => void;
    /**
     * Queues a MessageAbort (never waits).
     */
    sendAbort: (invocationId: number, kind: number, reason: number) => void;
};
export type CompletedMessage = {
    /**
     * 110 (Invocation) or 112 (InvocationResult).
     */
    innerType: number;
    /**
     * The inner message's body.
     */
    body: Uint8Array;
    /**
     * Budget bytes the caller now holds and must release once the message is processed.
     */
    reserved: number;
};
export type Stream = {
    total: number;
    innerType: number;
    kind: number;
    received: number;
    lastProgress: number;
    aborting: boolean;
    reserved: boolean;
    chunks: Uint8Array[];
};
/**
 * What the receiver needs from its session.
 *
 * @typedef {object} LargeMessageOwner
 * @property {(invocationId: number) => boolean} isCallOutstanding True if this side waits for the call's result.
 * @property {() => boolean} tryTakeInvocationCredit Takes one inbound credit for a large argument; false when none is left.
 * @property {() => void} returnInvocationCredit Returns a credit taken for an argument that will not be queued.
 * @property {(invocationId: number, kind: number, reason: number) => void} sendAbort Queues a MessageAbort (never waits).
 */
/**
 * A reassembled message.
 *
 * @typedef {object} CompletedMessage
 * @property {number} innerType 110 (Invocation) or 112 (InvocationResult).
 * @property {Uint8Array} body The inner message's body.
 * @property {number} reserved Budget bytes the caller now holds and must release once the message is processed.
 */
/**
 * @typedef {object} Stream
 * @property {number} total
 * @property {number} innerType
 * @property {number} kind
 * @property {number} received
 * @property {number} lastProgress
 * @property {boolean} aborting
 * @property {boolean} reserved
 * @property {Uint8Array[]} chunks
 */
export declare class LargeMessageReceiver {
    #private;
    /**
     * @param {LargeMessageOwner} owner
     * @param {import('./SessionBudget.js').SessionBudget} budget
     * @param {number} maxMessageSize This side's advertised `maxMessageSize`.
     * @param {() => number} now Clock time.
     */
    constructor(owner: LargeMessageOwner, budget: import('./SessionBudget.js').SessionBudget, maxMessageSize: number, now: () => number);
    /** Streams being received. @returns {number} */
    get count(): number;
    /**
     * Handles a MessageStart. Returns a disconnect reason when it breaks the protocol, else 0.
     * @param {import('./messages.js').MessageStart} start
     * @returns {number}
     */
    onStart(start: import('./messages.js').MessageStart): number;
    /**
     * Handles a MessageChunk. `payload` is copied.
     * @param {number} invocationId
     * @param {number} kind
     * @param {Uint8Array} payload
     * @returns {{ reason: number, completed: CompletedMessage | null }}
     */
    onChunk(invocationId: number, kind: number, payload: Uint8Array): {
        reason: number;
        completed: CompletedMessage | null;
    };
    /**
     * Handles an abort from the stream's sender: Cancelled ends the stream, Ack ends a stream this side refused. An
     * abort for an unknown key is ignored, as it can race with completion.
     * @param {{ invocationId: number, kind: number, reason: number }} abort
     * @returns {void}
     */
    onAbort(abort: {
        invocationId: number;
        kind: number;
        reason: number;
    }): void;
    /**
     * Snapshots of the streams being received (C# `LargeMessageReceiver.AddSnapshots`).
     * @param {number} now
     * @returns {import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]}
     */
    snapshots(now: number): import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[];
    /**
     * Describes a stream that received nothing for longer than `timeout` (§9); null when none did.
     * @param {number} now
     * @param {number} timeout
     * @returns {string | null}
     */
    findStalled(now: number, timeout: number): string | null;
    /** Drops every stream (the session ended). @returns {void} */
    clear(): void;
}
