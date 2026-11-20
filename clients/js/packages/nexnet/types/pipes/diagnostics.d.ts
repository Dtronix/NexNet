/**
 * Process-wide diagnostic counters of the pipe layer, for leak audits (H05): after every client has disconnected
 * they must all be zero.
 * @module
 */
export type PipeCounters = {
    /**
     * Pipes holding a receive window: created, open, or closing until both sides confirmed.
     */
    openPipes: number;
    /**
     * Received pipe bytes buffered and not yet consumed by a reader.
     */
    bufferedBytes: number;
};
/**
 * A snapshot of the counters.
 * @returns {PipeCounters}
 */
export declare function getPipeCounters(): PipeCounters;
/**
 * Adjusts a counter. Internal to the runtime.
 * @param {keyof PipeCounters} name
 * @param {number} delta
 * @returns {void}
 */
export declare function countPipes(name: keyof PipeCounters, delta: number): void;
