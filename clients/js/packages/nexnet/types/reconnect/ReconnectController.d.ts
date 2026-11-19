/**
 * Reconnects a client after its session is lost (C# `NexusClient.ConnectionClosed`): asks the policy whether the
 * reason qualifies, calls `nexus.onReconnecting()`, then waits each policy delay and retries with the reconnection
 * greeting (101) through `client._tryConnectCore(true)`, which calls `nexus.onConnected(true)` on success.
 *
 * Events (`CustomEvent`, dispatched on the controller): `reconnecting` `{ reason }` when a loop starts,
 * `reconnectattempt` `{ attempt }` before each attempt (1-based), `reconnected` `{ attempts }`, and
 * `reconnectfailed` `{ reason, attempts }` when the policy stops (a null delay).
 * @module
 */
export type ReconnectableClient = {
    config: import('../contract/config.js').ClientConfig;
    nexus: {
        onReconnecting(): void | Promise<void>;
    };
    state: import('../contract/enums.js').ConnectionStateValue;
    addEventListener: (type: string, listener: (event: Event) => void) => void;
    removeEventListener: (type: string, listener: (event: Event) => void) => void;
    _tryConnectCore: (isReconnecting: boolean, signal?: AbortSignal) => Promise<import('../client/NexusClient.js').ConnectionResult>;
};
export type ReconnectControllerOptions = {
    /**
     * Default: `client.config.reconnectionPolicy`,
     * else a {@link DefaultReconnectionPolicy}.
     */
    policy?: import('../contract/config.js').ReconnectionPolicy;
    /**
     * Default: `client.config.clock`, else the system clock.
     */
    clock?: import('../session/clock.js').Clock;
    /**
     * Default: `client.config.logger`.
     */
    logger?: import('../contract/config.js').NexusLogger | null;
    /**
     * Browser page lifecycle triggers
     * (`visibilitychange`, `online`, `freeze`/`resume`): true for the global `document` and `window`. Default false.
     */
    lifecycle?: boolean | import('./lifecycle.js').LifecycleTargets;
};
export declare class ReconnectController extends EventTarget {
    #private;
    /**
     * Starts watching `client` for lost sessions.
     * @param {ReconnectableClient} client
     * @param {ReconnectControllerOptions} [options]
     */
    constructor(client: ReconnectableClient, options?: ReconnectControllerOptions);
    /** The policy in use. @returns {import('../contract/config.js').ReconnectionPolicy} */
    get policy(): import('../contract/config.js').ReconnectionPolicy;
    /** True while a reconnection loop runs. @returns {boolean} */
    get isReconnecting(): boolean;
    /** True while the loop waits out a policy delay. @returns {boolean} */
    get isWaiting(): boolean;
    /** Attempts made by the current (or last) loop. @returns {number} */
    get attempts(): number;
    /** True once {@link ReconnectController#stop} was called. @returns {boolean} */
    get stopped(): boolean;
    /**
     * True when a session lost with `reason` would start a reconnection loop: the controller is not stopped and the
     * policy accepts the reason. A policy that throws counts as no.
     * @param {number} reason
     * @returns {boolean}
     */
    wouldReconnect(reason: number): boolean;
    /** Resolves when the current loop ends (at once when none runs). @returns {Promise<void>} */
    get idle(): Promise<void>;
    /**
     * Ends the current delay early, so the next attempt starts now. Used by the browser lifecycle triggers (the page
     * became visible, the network came back). Does nothing when no delay is pending.
     * @returns {boolean} True when a pending delay was cut short.
     */
    retryNow(): boolean;
    /**
     * Stops reconnecting for good: cancels a pending delay, stops listening to the client and detaches the lifecycle
     * triggers. An attempt already connecting is not interrupted. Idempotent.
     * @returns {Promise<void>} Resolves when the loop has ended.
     */
    stop(): Promise<void>;
    /** Same as {@link ReconnectController#stop}. @returns {Promise<void>} */
    [Symbol.asyncDispose](): Promise<void>;
}
