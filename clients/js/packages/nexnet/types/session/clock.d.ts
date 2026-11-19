/**
 * Time source for the session's timers. Tests inject a manual clock through `ClientConfig.clock`.
 * @module
 */
export type Clock = {
    /**
     * Milliseconds on a monotonic clock (`performance.now()` by default).
     */
    now: () => number;
    setTimeout: (callback: () => void, ms: number) => unknown;
    clearTimeout: (handle: unknown) => void;
    setInterval: (callback: () => void, ms: number) => unknown;
    clearInterval: (handle: unknown) => void;
};
/**
 * A monotonic clock and timer functions. `now()` is in milliseconds. Handles are opaque.
 *
 * @typedef {object} Clock
 * @property {() => number} now Milliseconds on a monotonic clock (`performance.now()` by default).
 * @property {(callback: () => void, ms: number) => unknown} setTimeout
 * @property {(handle: unknown) => void} clearTimeout
 * @property {(callback: () => void, ms: number) => unknown} setInterval
 * @property {(handle: unknown) => void} clearInterval
 */
/** The platform clock: `performance.now()` and the global timers. @type {Clock} */
export declare const systemClock: Clock;
/**
 * Resolves after `ms` on `clock`.
 * @param {Clock} clock
 * @param {number} ms
 * @returns {Promise<void>}
 */
export declare function delay(clock: Clock, ms: number): Promise<void>;
export type IntervalTimer = {
    stop(): void;
};
/**
 * Starts a repeating timer. With the system clock in a browser it runs in a dedicated Worker when one can be created
 * (a CSP may forbid blob workers), and falls back to `setInterval`. With any other clock it uses `clock.setInterval`.
 *
 * @param {Clock} clock
 * @param {number} ms
 * @param {() => void} callback
 * @returns {IntervalTimer}
 */
export declare function startIntervalTimer(clock: Clock, ms: number, callback: () => void): IntervalTimer;
