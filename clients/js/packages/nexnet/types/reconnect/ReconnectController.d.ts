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
export type ReconnectControllerEventMap = {
    /**
     * A loop starts.
     */
    reconnecting: CustomEvent<{
        reason: import('../contract/enums.js').DisconnectReasonValue;
    }>;
    /**
     * Before each attempt (1-based).
     */
    reconnectattempt: CustomEvent<{
        attempt: number;
    }>;
    reconnected: CustomEvent<{
        attempts: number;
    }>;
    /**
     *   The policy stopped (a null delay).
     */
    reconnectfailed: CustomEvent<{
        reason: import('../contract/enums.js').DisconnectReasonValue;
        attempts: number;
    }>;
};
export declare class ReconnectController extends EventTarget {
    #private;
    /**
     * Starts watching `client` for lost sessions.
     * @param {ReconnectableClient} client
     * @param {ReconnectControllerOptions} [options]
     */
    constructor(client: ReconnectableClient, options?: ReconnectControllerOptions);
    /**
     * Typed for the events of {@link ReconnectControllerEventMap}; any other type takes a plain listener.
     * @template {keyof ReconnectControllerEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: ReconnectController, event: ReconnectControllerEventMap[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener<K extends keyof ReconnectControllerEventMap>(type: K, listener: (this: ReconnectController, event: ReconnectControllerEventMap[K]) => void, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed for the events of {@link ReconnectControllerEventMap}; any other type takes a plain listener.
     * @template {keyof ReconnectControllerEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: ReconnectController, event: ReconnectControllerEventMap[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed like {@link ReconnectController#addEventListener}.
     * @template {keyof ReconnectControllerEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: ReconnectController, event: ReconnectControllerEventMap[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener<K extends keyof ReconnectControllerEventMap>(type: K, listener: (this: ReconnectController, event: ReconnectControllerEventMap[K]) => void, options?: boolean | EventListenerOptions): void;
    /**
     * Typed like {@link ReconnectController#addEventListener}.
     * @template {keyof ReconnectControllerEventMap} K
     * @overload
     * @param {K} type
     * @param {(this: ReconnectController, event: ReconnectControllerEventMap[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions): void;
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
     * Stops reconnecting for good: cancels a pending delay, aborts an attempt in progress (it opens no session and
     * `nexus.onConnected` is not called), stops listening to the client and detaches the lifecycle triggers. Idempotent.
     * @returns {Promise<void>} Resolves when the loop has ended.
     */
    stop(): Promise<void>;
    /** Same as {@link ReconnectController#stop}. @returns {Promise<void>} */
    [Symbol.asyncDispose](): Promise<void>;
}
