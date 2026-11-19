/**
 * The default reconnection policy. Mirrors C# `DefaultReconnectionPolicy` and `IReconnectionPolicy.ShouldReconnect`.
 * @module
 */
export type ReconnectionPolicy = import('../contract/config.js').ReconnectionPolicy;
/** @typedef {import('../contract/config.js').ReconnectionPolicy} ReconnectionPolicy */
/** Delays of the default policy, in milliseconds: 0, 2, 10 and 30 s (C# `DefaultReconnectionPolicy`). */
export declare const DEFAULT_RECONNECT_DELAYS: readonly number[];
/** Disconnect reasons the default policy reconnects for: SocketError, Timeout and ServerRestarting. */
export declare const DEFAULT_RECONNECT_REASONS: readonly number[];
/**
 * The default `shouldReconnect` of every policy (C# `IReconnectionPolicy.ShouldReconnect`): true for SocketError,
 * Timeout and ServerRestarting.
 * @param {import('../contract/enums.js').DisconnectReasonValue} reason
 * @returns {boolean}
 */
export declare function defaultShouldReconnect(reason: import('../contract/enums.js').DisconnectReasonValue): boolean;
/**
 * `policy.shouldReconnect(reason)`, or {@link defaultShouldReconnect} when the policy has none.
 * @param {import('../contract/config.js').ReconnectionPolicy} policy
 * @param {import('../contract/enums.js').DisconnectReasonValue} reason
 * @returns {boolean}
 */
export declare function policyShouldReconnect(policy: import('../contract/config.js').ReconnectionPolicy, reason: import('../contract/enums.js').DisconnectReasonValue): boolean;
/**
 * Retries after 0, 2, 10 and 30 seconds, then every 30 seconds (`continuousRetry`). Reconnects only for the reasons in
 * {@link DefaultReconnectionPolicy#reconnectReasons}.
 *
 * @implements {ReconnectionPolicy}
 */
export declare class DefaultReconnectionPolicy implements ReconnectionPolicy {
    /** Delays in milliseconds, one per attempt. @readonly @type {readonly number[]} */
    readonly delays: readonly number[];
    /** When true the last delay repeats forever; otherwise the policy stops after the last one. @readonly @type {boolean} */
    readonly continuousRetry: boolean;
    /**
     * Disconnect reasons the client reconnects for. Add `ServerShutdown` to reconnect after the server stops and starts
     * again; `Graceful` and `Authentication` are best left out.
     * @readonly @type {ReadonlySet<number>}
     */
    readonly reconnectReasons: ReadonlySet<number>;
    /**
     * @param {object} [options]
     * @param {readonly number[]} [options.delays] Default {@link DEFAULT_RECONNECT_DELAYS}.
     * @param {boolean} [options.continuousRetry] Default true.
     * @param {Iterable<number>} [options.reconnectReasons] Default {@link DEFAULT_RECONNECT_REASONS}.
     */
    constructor({ delays, continuousRetry, reconnectReasons }?: {
        delays?: readonly number[];
        continuousRetry?: boolean;
        reconnectReasons?: Iterable<number>;
    });
    /**
     * @param {number} attempt 0-based.
     * @returns {number | null}
     */
    reconnectDelay(attempt: number): number | null;
    /**
     * @param {import('../contract/enums.js').DisconnectReasonValue} reason
     * @returns {boolean}
     */
    shouldReconnect(reason: import('../contract/enums.js').DisconnectReasonValue): boolean;
}
