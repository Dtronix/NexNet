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

import { ConnectionState, DisconnectReason } from '../contract/enums.js';
import { systemClock } from '../session/clock.js';
import { countOps } from '../diagnostics/counters.js';
import { DefaultReconnectionPolicy, policyShouldReconnect } from './DefaultReconnectionPolicy.js';
import { attachBrowserLifecycle } from './lifecycle.js';

/**
 * What the controller needs of a client. {@link import('../client/NexusClient.js').NexusClient} satisfies it.
 * @typedef {object} ReconnectableClient
 * @property {import('../contract/config.js').ClientConfig} config
 * @property {{ onReconnecting(): void | Promise<void> }} nexus
 * @property {import('../contract/enums.js').ConnectionStateValue} state
 * @property {(type: string, listener: (event: Event) => void) => void} addEventListener
 * @property {(type: string, listener: (event: Event) => void) => void} removeEventListener
 * @property {(isReconnecting: boolean, signal?: AbortSignal) => Promise<import('../client/NexusClient.js').ConnectionResult>} _tryConnectCore
 */

/**
 * @typedef {object} ReconnectControllerOptions
 * @property {import('../contract/config.js').ReconnectionPolicy} [policy] Default: `client.config.reconnectionPolicy`,
 *   else a {@link DefaultReconnectionPolicy}.
 * @property {import('../session/clock.js').Clock} [clock] Default: `client.config.clock`, else the system clock.
 * @property {import('../contract/config.js').NexusLogger | null} [logger] Default: `client.config.logger`.
 * @property {boolean | import('./lifecycle.js').LifecycleTargets} [lifecycle] Browser page lifecycle triggers
 *   (`visibilitychange`, `online`, `freeze`/`resume`): true for the global `document` and `window`. Default false.
 */

/**
 * The events of a {@link ReconnectController}, for `addEventListener`.
 * @typedef {object} ReconnectControllerEventMap
 * @property {CustomEvent<{ reason: import('../contract/enums.js').DisconnectReasonValue }>} reconnecting A loop starts.
 * @property {CustomEvent<{ attempt: number }>} reconnectattempt Before each attempt (1-based).
 * @property {CustomEvent<{ attempts: number }>} reconnected
 * @property {CustomEvent<{ reason: import('../contract/enums.js').DisconnectReasonValue, attempts: number }>} reconnectfailed
 *   The policy stopped (a null delay).
 */

/** @param {number} reason @returns {string} */
function reasonName(reason) {
  const names = /** @type {Record<string, number>} */ (DisconnectReason);
  return Object.keys(names).find((key) => names[key] === reason) ?? `reason ${reason}`;
}

export class ReconnectController extends EventTarget {
  /** @type {ReconnectableClient} */
  #client;
  /** @type {import('../contract/config.js').ReconnectionPolicy} */
  #policy;
  /** @type {import('../session/clock.js').Clock} */
  #clock;
  /** @type {import('../contract/config.js').NexusLogger | null} */
  #logger;
  #stopped = false;
  #running = false;
  #attempts = 0;
  /** @type {{ handle: unknown, resolve: (proceed: boolean) => void } | null} */
  #wait = null;
  /** @type {Promise<void>} */
  #loop = Promise.resolve();
  /** @type {(() => void) | null} */
  #detachLifecycle = null;
  /** @type {(event: Event) => void} */
  #onDisconnected;

  /**
   * Starts watching `client` for lost sessions.
   * @param {ReconnectableClient} client
   * @param {ReconnectControllerOptions} [options]
   */
  constructor(client, options = {}) {
    super();
    this.#client = client;
    this.#policy = options.policy ?? client.config.reconnectionPolicy ?? new DefaultReconnectionPolicy();
    if (typeof this.#policy?.reconnectDelay !== 'function') throw new TypeError('policy.reconnectDelay must be a function');
    this.#clock = options.clock ?? client.config.clock ?? systemClock;
    this.#logger = options.logger !== undefined ? options.logger : client.config.logger ?? null;
    this.#onDisconnected = (event) => {
      const reason = /** @type {CustomEvent<{ reason: number }>} */ (event).detail?.reason ?? DisconnectReason.SocketError;
      this.#start(reason);
    };
    client.addEventListener('disconnected', this.#onDisconnected);
    countOps('subscriptions', 1);
    if (options.lifecycle) {
      this.#detachLifecycle = attachBrowserLifecycle(this, options.lifecycle === true ? undefined : options.lifecycle);
    }
  }

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
  addEventListener(type, listener, options) {
    super.addEventListener(type, listener, options);
  }

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
  removeEventListener(type, listener, options) {
    super.removeEventListener(type, listener, options);
  }

  /** The policy in use. @returns {import('../contract/config.js').ReconnectionPolicy} */
  get policy() {
    return this.#policy;
  }

  /** True while a reconnection loop runs. @returns {boolean} */
  get isReconnecting() {
    return this.#running;
  }

  /** True while the loop waits out a policy delay. @returns {boolean} */
  get isWaiting() {
    return this.#wait !== null;
  }

  /** Attempts made by the current (or last) loop. @returns {number} */
  get attempts() {
    return this.#attempts;
  }

  /** True once {@link ReconnectController#stop} was called. @returns {boolean} */
  get stopped() {
    return this.#stopped;
  }

  /**
   * True when a session lost with `reason` would start a reconnection loop: the controller is not stopped and the
   * policy accepts the reason. A policy that throws counts as no.
   * @param {number} reason
   * @returns {boolean}
   */
  wouldReconnect(reason) {
    if (this.#stopped) return false;
    try {
      return policyShouldReconnect(this.#policy, reason);
    } catch {
      return false;
    }
  }

  /** Resolves when the current loop ends (at once when none runs). @returns {Promise<void>} */
  get idle() {
    return this.#loop;
  }

  /**
   * Ends the current delay early, so the next attempt starts now. Used by the browser lifecycle triggers (the page
   * became visible, the network came back). Does nothing when no delay is pending.
   * @returns {boolean} True when a pending delay was cut short.
   */
  retryNow() {
    const wait = this.#wait;
    if (wait === null) return false;
    this.#endWait(true);
    return true;
  }

  /**
   * Stops reconnecting for good: cancels a pending delay, stops listening to the client and detaches the lifecycle
   * triggers. An attempt already connecting is not interrupted. Idempotent.
   * @returns {Promise<void>} Resolves when the loop has ended.
   */
  stop() {
    if (!this.#stopped) {
      this.#stopped = true;
      this.#client.removeEventListener('disconnected', this.#onDisconnected);
      countOps('subscriptions', -1);
      this.#detachLifecycle?.();
      this.#detachLifecycle = null;
      this.#endWait(false);
    }
    return this.#loop;
  }

  /** Same as {@link ReconnectController#stop}. @returns {Promise<void>} */
  [Symbol.asyncDispose]() {
    return this.stop();
  }

  /**
   * Starts a loop for a session lost with `reason`, unless one runs, the controller is stopped or the policy declines.
   * @param {number} reason
   * @returns {void}
   */
  #start(reason) {
    if (this.#stopped || this.#running) return;
    let should;
    try {
      should = policyShouldReconnect(this.#policy, reason);
    } catch (error) {
      this.#log('error', 'Reconnection policy shouldReconnect threw.', error);
      return;
    }
    if (!should) {
      this.#log('information', `Can't reconnect with ${reasonName(reason)} disconnection reason.`);
      return;
    }
    this.#running = true;
    this.#attempts = 0;
    this.#loop = this.#run(reason);
  }

  /**
   * @param {number} reason
   * @returns {Promise<void>}
   */
  async #run(reason) {
    try {
      await this.#runCore(reason);
    } finally {
      // Cleared synchronously when the loop ends, so a session lost right after a reconnect starts a new loop.
      this.#running = false;
    }
  }

  /**
   * @param {number} reason
   * @returns {Promise<void>}
   */
  async #runCore(reason) {
    this.#log('information', `Connection lost. Reconnecting. ${reasonName(reason)}`);
    this.#dispatch('reconnecting', { reason });
    await this.#callOnReconnecting();

    for (;;) {
      if (this.#stopped) return;
      if (this.#isConnected()) return;
      /** @type {number | null | undefined} */
      let delay;
      try {
        delay = this.#policy.reconnectDelay(this.#attempts);
      } catch (error) {
        this.#log('error', 'Reconnection policy reconnectDelay threw.', error);
        delay = null;
      }
      if (delay === null || delay === undefined) {
        this.#log('information', `Reconnection stopped by the policy after ${this.#attempts} attempts.`);
        this.#dispatch('reconnectfailed', { reason, attempts: this.#attempts });
        return;
      }
      if (typeof delay !== 'number' || !(delay >= 0)) {
        this.#log('error', `Reconnection policy returned an invalid delay: ${String(delay)}.`);
        this.#dispatch('reconnectfailed', { reason, attempts: this.#attempts });
        return;
      }
      if (!(await this.#delay(delay))) return;
      if (this.#isConnected()) return;

      this.#attempts++;
      this.#dispatch('reconnectattempt', { attempt: this.#attempts });
      this.#log('trace', `Reconnection attempt ${this.#attempts}`);
      /** @type {import('../client/NexusClient.js').ConnectionResult} */
      let result;
      try {
        result = await this.#client._tryConnectCore(true);
      } catch (error) {
        this.#log('error', 'Reconnection attempt threw.', error);
        continue;
      }
      if (result.success) {
        this.#log('information', 'Reconnected');
        this.#dispatch('reconnected', { attempts: this.#attempts });
        return;
      }
      if (result.error !== undefined) {
        this.#log('error', `Reconnection failed with an error. State: ${result.state}`, result.error);
      }
    }
  }

  /** @returns {boolean} */
  #isConnected() {
    return this.#client.state === ConnectionState.Connected;
  }

  /** Calls `nexus.onReconnecting()`; a throw or rejection is logged. @returns {Promise<void>} */
  async #callOnReconnecting() {
    try {
      await this.#client.nexus.onReconnecting();
    } catch (error) {
      this.#log('error', 'onReconnecting threw.', error);
    }
  }

  /**
   * Waits `ms` on the clock. Resolves true to go on, false when stopped.
   * @param {number} ms
   * @returns {Promise<boolean>}
   */
  #delay(ms) {
    return new Promise((resolve) => {
      countOps('timers', 1);
      const handle = this.#clock.setTimeout(() => this.#endWait(true), ms);
      this.#wait = { handle, resolve };
    });
  }

  /** @param {boolean} proceed */
  #endWait(proceed) {
    const wait = this.#wait;
    if (wait === null) return;
    this.#wait = null;
    this.#clock.clearTimeout(wait.handle);
    countOps('timers', -1);
    wait.resolve(proceed && !this.#stopped);
  }

  /**
   * @param {string} type
   * @param {object} detail
   */
  #dispatch(type, detail) {
    this.dispatchEvent(new CustomEvent(type, { detail }));
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   */
  #log(level, message, error) {
    try {
      this.#logger?.log(level, message, error);
    } catch {
      // A logger must never break reconnection.
    }
  }
}
