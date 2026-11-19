/**
 * Process-wide diagnostic counters of the invocation layer, for leak audits (H05): after every client has
 * disconnected they must all be zero.
 * @module
 */

/**
 * @typedef {object} InvocationCounters
 * @property {number} pendingInvocations Outbound calls not yet completed (waiting for credit, sending, or waiting for
 *   their result).
 * @property {number} reservedInvocationIds Outbound invocation IDs reserved (awaiting a result, or a late result
 *   after a cancel).
 * @property {number} creditWaiters Outbound calls parked waiting for invocation credit.
 * @property {number} inboundInvocations Server-to-client invocations queued or running.
 * @property {number} largeMessages Large messages being sent or received.
 */

const counters = { pendingInvocations: 0, reservedInvocationIds: 0, creditWaiters: 0, inboundInvocations: 0, largeMessages: 0 };

/**
 * A snapshot of the counters.
 * @returns {InvocationCounters}
 */
export function getInvocationCounters() {
  return { ...counters };
}

/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof InvocationCounters} name
 * @param {number} delta
 * @returns {void}
 */
export function countInvocation(name, delta) {
  counters[name] += delta;
}
