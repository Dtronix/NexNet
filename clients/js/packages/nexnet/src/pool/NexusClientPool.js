/**
 * Client pool. Mirrors C# `NexusClientPool`: up to `maxConnections` clients, rented one caller at a time and
 * returned by releasing the rental; idle clients beyond `minIdleConnections` are disconnected after `maxIdleTime`, and
 * unhealthy (disconnected) ones are dropped by a periodic health check.
 * @module
 */

import { ConnectionResultState, ConnectionState } from '../contract/enums.js';
import { NexusError, cancellationError } from '../contract/errors.js';
import { NexusClient } from '../client/NexusClient.js';
import { systemClock } from '../session/clock.js';
import { countOps } from '../diagnostics/counters.js';

/**
 * {@link import('../contract/pool.js').NexusClientPoolConfig}, plus the client class to create. The Node entry's pool
 * uses the Node client; the platform-neutral pool defaults to the WebSocket-only {@link NexusClient}.
 * @typedef {import('../contract/pool.js').NexusClientPoolConfig & { clientClass?: typeof NexusClient }} PoolConfig
 */

/**
 * A rented client: {@link import('../contract/pool.js').RentedNexusClient}, also disposable (`using`) to release it.
 * @template {import('../contract/proxy.js').ProxyBase} TProxy
 * @typedef {import('../contract/pool.js').RentedNexusClient<TProxy> & { [Symbol.dispose](): void }} RentedClient
 */

/** A connect of a newly created pooled client failed. Mirrors C# `ClientPoolConnectionException`. */
export class NexusClientPoolConnectionError extends NexusError {
  /**
   * @param {string} message
   * @param {import('../client/NexusClient.js').ConnectionResult} result
   */
  constructor(message, result) {
    super(message, { cause: result.error });
    /** The failed connection result. */
    this.result = result;
  }
}

/** @returns {Error} */
function disposedError() {
  return new NexusError('The NexusClientPool is disposed.');
}

/**
 * A client owned by the pool.
 * @typedef {object} PooledClient
 * @property {NexusClient<any, any>} client
 * @property {number} lastUsed Clock time of the last rent or return.
 * @property {boolean} disposed
 */

/**
 * @param {PooledClient} pooled
 * @returns {boolean}
 */
function isHealthy(pooled) {
  return !pooled.disposed && pooled.client.state === ConnectionState.Connected;
}

/**
 * Pool of connected clients to one server.
 *
 * @template {import('../contract/nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('../contract/proxy.js').ProxyBase} TProxy
 */
export class NexusClientPool {
  /**
   * The client class pooled clients are created with when `config.clientClass` is not set.
   * @type {typeof NexusClient}
   */
  static clientClass = NexusClient;

  /**
   * @readonly
   * @type {import('../contract/pool.js').NexusClientPoolConfig}
   */
  config;

  /**
   * @protected
   * @readonly
   * @type {() => TNexus}
   */
  _nexusFactory;

  /**
   * @protected
   * @readonly
   * @type {import('../contract/handshake.js').ClientBinding<TProxy>}
   */
  _binding;

  #maxConnections;
  #maxIdleTime;
  #minIdleConnections;
  /** @type {import('../session/clock.js').Clock} */
  #clock;
  /** Idle clients, oldest return first. @type {PooledClient[]} */
  #available = [];
  #permits;
  /** @type {Array<{ resolve: () => void, reject: (error: unknown) => void, signal?: AbortSignal, onAbort: (() => void) | null }>} */
  #waiters = [];
  /** @type {unknown} */
  #timer;
  #disposed = false;

  /**
   * @param {import('../contract/pool.js').NexusClientPoolConfig} config
   * @param {() => TNexus} nexusFactory
   * @param {import('../contract/handshake.js').ClientBinding<TProxy>} binding Supplied by generated code.
   */
  constructor(config, nexusFactory, binding) {
    if (typeof config !== 'object' || config === null) throw new TypeError('config is required');
    if (typeof config.clientConfig !== 'object' || config.clientConfig === null) throw new TypeError('config.clientConfig is required');
    if (typeof nexusFactory !== 'function') throw new TypeError('nexusFactory must be a function');
    const maxConnections = config.maxConnections ?? 10;
    const maxIdleTime = config.maxIdleTime ?? 120_000;
    const minIdleConnections = config.minIdleConnections ?? 1;
    if (!Number.isInteger(maxConnections) || maxConnections <= 0) throw new RangeError('maxConnections must be a positive integer');
    if (!Number.isInteger(minIdleConnections) || minIdleConnections < 0) throw new RangeError('minIdleConnections must be a non-negative integer');
    if (typeof maxIdleTime !== 'number' || !(maxIdleTime >= 0)) throw new RangeError('maxIdleTime must be a number >= 0');

    this.config = config;
    this._nexusFactory = nexusFactory;
    this._binding = binding;
    this.#maxConnections = maxConnections;
    this.#maxIdleTime = maxIdleTime;
    this.#minIdleConnections = minIdleConnections;
    this.#permits = maxConnections;
    this.#clock = config.clientConfig.clock ?? systemClock;

    // More frequent than the idle timeout, within reasonable bounds (C#).
    const interval = Math.max(100, Math.min(30_000, maxIdleTime / 4));
    this.#timer = this.#clock.setInterval(() => this.#healthCheck(), interval);
    countOps('timers', 1);
  }

  /** @returns {number} */
  get maxConnections() {
    return this.#maxConnections;
  }

  /** Idle clients ready to rent. @returns {number} */
  get availableConnections() {
    return this.#available.length;
  }

  /**
   * Rents a connected client, connecting a new one when none is idle and the pool is not full. Waits in FIFO order
   * while `maxConnections` clients are rented. Rejects with the abort reason when `signal` aborts, and with
   * {@link NexusClientPoolConnectionError} when a new client fails to connect.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<RentedClient<TProxy>>}
   */
  async rent(options) {
    const signal = options?.signal;
    if (this.#disposed) throw disposedError();
    await this.#acquire(signal);
    if (this.#disposed) {
      this.#releasePermit();
      throw disposedError();
    }
    try {
      /** @type {PooledClient | null} */
      let pooled = null;
      while (this.#available.length > 0) {
        const candidate = /** @type {PooledClient} */ (this.#available.shift());
        if (isHealthy(candidate)) {
          candidate.lastUsed = this.#clock.now();
          pooled = candidate;
          break;
        }
        void this.#disposeClient(candidate);
      }

      if (pooled === null) {
        const ctor = /** @type {PoolConfig} */ (this.config).clientClass
          ?? /** @type {typeof NexusClientPool} */ (this.constructor).clientClass;
        const client = ctor.create(this.config.clientConfig, this._nexusFactory(), this._binding);
        pooled = { client, lastUsed: this.#clock.now(), disposed: false };
        countOps('pooledClients', 1);
        const result = await client.tryConnect({ signal });
        if (!result.success) {
          await this.#disposeClient(pooled);
          if (result.state === ConnectionResultState.Canceled) throw cancellationError(signal);
          this.#log('error', 'Failed to connect after renting.', result.error);
          throw new NexusClientPoolConnectionError('Failed to connect after renting.', result);
        }
      }
      return this.#rental(pooled);
    } catch (error) {
      this.#releasePermit();
      throw error;
    }
  }

  /**
   * Returns a connector that resolves a collection on a pooled client. Each `getCollection()` returns the client
   * the connector holds to the pool, then rents one, so the connector holds at most one rental; `release()` (or
   * disposing the connector) returns it.
   * @template T
   * @param {(proxy: TProxy) => import('../contract/collections.js').NexusList<T>} selector
   * @returns {{ getCollection(): Promise<import('../contract/collections.js').NexusList<T>>, release(): void, [Symbol.dispose](): void }}
   */
  collectionConnector(selector) {
    /** @type {import('../contract/pool.js').RentedNexusClient<TProxy> | null} */
    let held = null;
    const release = () => {
      const rental = held;
      held = null;
      rental?.release();
    };
    return {
      getCollection: async () => {
        // Return the earlier rental before renting again: holding it while waiting for a permit would block for good
        // on a pool of one connection.
        release();
        const rental = await this.rent();
        // A concurrent call may have stored its rental meanwhile: the connector holds the latest one only.
        const raced = held;
        held = rental;
        raced?.release();
        return selector(rental.proxy);
      },
      release,
      [Symbol.dispose]: release,
    };
  }

  /** Disconnects every idle client and stops the health check. Rented clients are disconnected when released. @returns {Promise<void>} */
  async [Symbol.asyncDispose]() {
    if (this.#disposed) return;
    this.#disposed = true;
    this.#clock.clearInterval(this.#timer);
    countOps('timers', -1);
    for (const waiter of this.#waiters.splice(0)) {
      if (waiter.onAbort) waiter.signal?.removeEventListener('abort', waiter.onAbort);
      waiter.reject(disposedError());
    }
    const idle = this.#available.splice(0);
    await Promise.all(idle.map((pooled) => this.#disposeClient(pooled)));
  }

  /**
   * @param {PooledClient} pooled
   * @returns {RentedClient<TProxy>}
   */
  #rental(pooled) {
    /** @type {PooledClient | null} */
    let current = pooled;
    const pool = this;
    return {
      get proxy() {
        if (current === null) throw new NexusError('The rented client was released.');
        return /** @type {TProxy} */ (current.client.proxy);
      },
      get state() {
        return current === null ? ConnectionState.Disconnected : current.client.state;
      },
      get disconnected() {
        return current === null ? Promise.resolve() : current.client.disconnected;
      },
      async ensureConnected(options) {
        if (current === null) return false;
        if (current.client.state === ConnectionState.Connected) return true;
        const result = await current.client.tryConnect(options);
        return result.success;
      },
      release() {
        const returned = current;
        current = null;
        if (returned !== null) pool.#return(returned);
      },
      [Symbol.dispose]() {
        this.release();
      },
    };
  }

  /** @param {PooledClient} pooled */
  #return(pooled) {
    if (this.#disposed || !isHealthy(pooled)) {
      void this.#disposeClient(pooled);
    } else {
      pooled.lastUsed = this.#clock.now();
      this.#available.push(pooled);
    }
    this.#releasePermit();
  }

  #healthCheck() {
    if (this.#disposed) return;
    const now = this.#clock.now();
    let healthy = 0;
    for (const pooled of [...this.#available]) {
      let keep = false;
      if (isHealthy(pooled)) {
        keep = now - pooled.lastUsed < this.#maxIdleTime || healthy < this.#minIdleConnections;
        if (keep) healthy++;
      }
      if (!keep) {
        this.#available.splice(this.#available.indexOf(pooled), 1);
        void this.#disposeClient(pooled);
      }
    }
  }

  /**
   * @param {PooledClient} pooled
   * @returns {Promise<void>}
   */
  async #disposeClient(pooled) {
    if (pooled.disposed) return;
    pooled.disposed = true;
    countOps('pooledClients', -1);
    try {
      await pooled.client.disconnect();
    } catch (error) {
      this.#log('warning', 'Disconnecting a pooled client failed.', error);
    }
  }

  /**
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<void>}
   */
  #acquire(signal) {
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    if (this.#permits > 0 && this.#waiters.length === 0) {
      this.#permits--;
      return Promise.resolve();
    }
    return new Promise((resolve, reject) => {
      /** @type {{ resolve: () => void, reject: (error: unknown) => void, signal?: AbortSignal, onAbort: (() => void) | null }} */
      const waiter = { resolve, reject, signal, onAbort: null };
      if (signal) {
        waiter.onAbort = () => {
          const index = this.#waiters.indexOf(waiter);
          if (index >= 0) this.#waiters.splice(index, 1);
          reject(cancellationError(signal));
        };
        signal.addEventListener('abort', waiter.onAbort, { once: true });
      }
      this.#waiters.push(waiter);
    });
  }

  #releasePermit() {
    const next = this.#waiters.shift();
    if (next) {
      if (next.onAbort) next.signal?.removeEventListener('abort', next.onAbort);
      next.resolve();
    } else {
      this.#permits++;
    }
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   */
  #log(level, message, error) {
    try {
      this.config.clientConfig.logger?.log(level, message, error);
    } catch {
      // A logger must never break the pool.
    }
  }
}
