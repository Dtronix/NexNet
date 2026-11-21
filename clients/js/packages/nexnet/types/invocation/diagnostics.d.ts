/**
 * Process-wide diagnostic counters of the invocation layer, for leak audits: after every client has
 * disconnected they must all be zero.
 * @module
 */
export type InvocationCounters = {
    /**
     * Outbound calls not yet completed (waiting for credit, sending, or waiting for
     * their result).
     */
    pendingInvocations: number;
    /**
     * Outbound invocation IDs reserved (awaiting a result, or a late result
     * after a cancel).
     */
    reservedInvocationIds: number;
    /**
     * Outbound calls parked waiting for invocation credit.
     */
    creditWaiters: number;
    /**
     * Server-to-client invocations queued or running.
     */
    inboundInvocations: number;
    /**
     * Large messages being sent or received.
     */
    largeMessages: number;
};
/**
 * A snapshot of the counters.
 * @returns {InvocationCounters}
 */
export declare function getInvocationCounters(): InvocationCounters;
/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof InvocationCounters} name
 * @param {number} delta
 * @returns {void}
 */
export declare function countInvocation(name: keyof InvocationCounters, delta: number): void;
