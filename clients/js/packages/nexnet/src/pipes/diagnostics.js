/**
 * Process-wide diagnostic counters of the pipe layer, for leak audits: after every client has disconnected
 * they must all be zero.
 * @module
 */

/**
 * @typedef {object} PipeCounters
 * @property {number} openPipes Pipes holding a receive window: created, open, or closing until both sides confirmed.
 * @property {number} bufferedBytes Received pipe bytes buffered and not yet consumed by a reader.
 */

const counters = { openPipes: 0, bufferedBytes: 0 };

/**
 * A snapshot of the counters.
 * @returns {PipeCounters}
 */
export function getPipeCounters() {
  return { ...counters };
}

/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof PipeCounters} name
 * @param {number} delta
 * @returns {void}
 */
export function countPipes(name, delta) {
  counters[name] += delta;
}
