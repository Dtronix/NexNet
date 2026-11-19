/**
 * A session's receive memory budget (protocol spec §9, C# `SessionBudget`). Counts reservations, not allocations:
 * the invocation queue, every large message being received and (J08) every pipe window are reserved from it.
 * @module
 */
export declare class SessionBudget {
    #private;
    /** @param {number} limit Total bytes that may be reserved at once. */
    constructor(limit: number);
    /** @returns {number} */
    get limit(): number;
    /** Bytes currently reserved. @returns {number} */
    get reserved(): number;
    /**
     * Reserves `bytes` if the budget can hold them, all or nothing.
     * @param {number} bytes
     * @returns {boolean}
     */
    tryReserve(bytes: number): boolean;
    /**
     * Reserves `bytes`, all or nothing, waiting in FIFO order behind earlier waiters until the budget has room.
     * Resolves false when `signal` aborts first or the request can never fit.
     * @param {number} bytes
     * @param {AbortSignal} [signal]
     * @returns {Promise<boolean>}
     */
    reserve(bytes: number, signal?: AbortSignal): Promise<boolean>;
    /**
     * Returns `bytes` reserved earlier, then serves waiting reservations that now fit.
     * @param {number} bytes
     * @returns {void}
     */
    release(bytes: number): void;
    /** Fails every waiting reservation (the session ended). @returns {void} */
    close(): void;
}
