/**
 * Coalesced control replies (C# `SessionControlSender`): ping replies, pipe window updates and close
 * acknowledgements, large-message aborts, invocation cancellations and invocation credit. Requests only record what
 * is owed; the frames are built when the write queue flushes, so any number of requests between two flushes cost at
 * most one frame of each kind (one per pipe, abort key or invocation), and credit rides along with other writes.
 * @module
 */
export type PipeControlSource = {
    /**
     * Returns the frames to send now (window updates, state updates).
     */
    takePendingControl: () => Uint8Array[];
};
/**
 * A pipe with control state to send (J08). The pipe queues itself at most once until drained.
 *
 * @typedef {object} PipeControlSource
 * @property {() => Uint8Array[]} takePendingControl Returns the frames to send now (window updates, state updates).
 */
export declare class ControlSender {
    #private;
    /**
     * @param {() => void} schedule Asks the write queue to flush.
     * @param {number} invocationCreditThreshold Credit is returned in batches of at least this much (half the advertised credits).
     */
    constructor(schedule: () => void, invocationCreditThreshold: number);
    /** Requests a `Ping` reply. Requests before the next flush send one. @returns {void} */
    requestPong(): void;
    /**
     * Requests that a pipe's pending control state be sent.
     * @param {PipeControlSource} pipe
     * @returns {void}
     */
    requestPipeControl(pipe: PipeControlSource): void;
    /**
     * Requests a `MessageAbort`; at most one per (invocation ID, kind) is pending, the latest reason wins.
     * @param {number} invocationId @param {number} kind @param {number} reason
     * @returns {void}
     */
    requestAbort(invocationId: number, kind: number, reason: number): void;
    /**
     * Requests an `InvocationCancellation`; one per invocation ID.
     * @param {number} invocationId
     * @returns {void}
     */
    requestInvocationCancellation(invocationId: number): void;
    /**
     * Returns invocation credit to the peer. Sent once the pending credit reaches the threshold.
     * @param {number} [credits]
     * @returns {void}
     */
    returnInvocationCredit(credits?: number): void;
    /** Invocation credit owed and not yet sent. @returns {number} */
    get pendingInvocationCredit(): number;
    /** True when {@link ControlSender#drain} has frames to return. @returns {boolean} */
    get hasPending(): boolean;
    /**
     * Takes every pending item as frames. Called by the write queue at flush time.
     * @returns {Uint8Array[]}
     */
    drain(): Uint8Array[];
    /** Drops everything pending (the session ended). @returns {void} */
    clear(): void;
}
