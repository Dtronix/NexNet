/**
 * Process-wide diagnostic counters of the session layer, for leak audits (H05): after every client has disconnected
 * they must all be zero.
 * @module
 */
export type SessionCounters = {
    /**
     * Sessions created and not yet disconnected.
     */
    openSessions: number;
    /**
     * Session timers running (handshake, keepalive, disconnect delay).
     */
    timers: number;
    /**
     * Frames queued or being written and not yet flushed.
     */
    pendingWrites: number;
};
/**
 * A snapshot of the counters.
 * @returns {SessionCounters}
 */
export declare function getSessionCounters(): SessionCounters;
/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof SessionCounters} name
 * @param {number} delta
 * @returns {void}
 */
export declare function countSession(name: keyof SessionCounters, delta: number): void;
