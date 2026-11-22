/**
 * Process-wide diagnostic counters of the session layer, for leak audits: after every client has disconnected
 * they must all be zero.
 * @module
 */

/**
 * @typedef {object} SessionCounters
 * @property {number} openSessions Sessions created and not yet disconnected.
 * @property {number} timers Session timers running (handshake, keepalive, disconnect delay).
 * @property {number} pendingWrites Frames queued or being written and not yet flushed.
 */

const counters = { openSessions: 0, timers: 0, pendingWrites: 0 };

/**
 * A snapshot of the counters.
 * @returns {SessionCounters}
 */
export function getSessionCounters() {
  return { ...counters };
}

/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof SessionCounters} name
 * @param {number} delta
 * @returns {void}
 */
export function countSession(name, delta) {
  counters[name] += delta;
}
