/**
 * Counting gate for outbound invocation credits (protocol spec §8, C# `CreditGate`). Waiters are served in FIFO
 * order; a credit released while calls wait goes to the oldest one, so a credit that is available is never one a
 * waiter is owed.
 * @module
 */
export type Waiter = {
    resolve: (acquired: boolean) => void;
    reject: (error: unknown) => void;
    signal: AbortSignal | undefined;
    onAbort: (() => void) | null;
};
/**
 * @typedef {object} Waiter
 * @property {(acquired: boolean) => void} resolve
 * @property {(error: unknown) => void} reject
 * @property {AbortSignal | undefined} signal
 * @property {(() => void) | null} onAbort
 */
export declare class CreditGate {
    #private;
    /** Credits available now. @returns {number} */
    get available(): number;
    /** Calls waiting for a credit. @returns {number} */
    get waiting(): number;
    /** True once {@link CreditGate#close} ran. @returns {boolean} */
    get closed(): boolean;
    /**
     * Takes a credit if one is available, without waiting.
     * @returns {boolean}
     */
    tryTake(): boolean;
    /**
     * Takes a credit, waiting in FIFO order. Resolves true once taken, false when the gate closes first (the session
     * ended). Rejects with the cancellation error of `signal` when it aborts first; the wait then holds no place.
     * @param {AbortSignal} [signal]
     * @returns {Promise<boolean>}
     */
    wait(signal?: AbortSignal): Promise<boolean>;
    /**
     * Releases credits: waiters are served first, in FIFO order; the rest become available.
     * @param {number} [credits]
     * @returns {void}
     */
    release(credits?: number): void;
    /**
     * Ends every wait with false; later waits that would park resolve false at once. Credits still available can be
     * taken.
     * @returns {void}
     */
    close(): void;
}
