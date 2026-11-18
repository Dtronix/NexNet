/**
 * Client pool (D20). J10 implements it in `src/pool/`. Mirrors C# `NexusClientPool`.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * @typedef {object} NexusClientPoolConfig
 * @property {import('./config.js').ClientConfig} clientConfig
 * @property {number} [maxConnections] Default 10.
 * @property {number} [maxIdleTime] Milliseconds; default 120,000.
 * @property {number} [minIdleConnections] Default 1.
 */

/**
 * A client rented from a pool. Disposing returns it to the pool.
 *
 * @template {import('./proxy.js').ProxyBase} TProxy
 * @typedef {object} RentedNexusClient
 * @property {TProxy} proxy
 * @property {import('./enums.js').ConnectionStateValue} state
 * @property {Promise<void>} disconnected
 * @property {(options?: { signal?: AbortSignal }) => Promise<boolean>} ensureConnected
 * @property {() => void} release Returns the client to the pool.
 */

/**
 * Pool of connected clients to one server.
 *
 * @template {import('./nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('./proxy.js').ProxyBase} TProxy
 */
export class NexusClientPool {
  /**
   * @readonly
   * @type {NexusClientPoolConfig}
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
   * @type {import('./handshake.js').ClientBinding<TProxy>}
   */
  _binding;

  /**
   * @param {NexusClientPoolConfig} config
   * @param {() => TNexus} nexusFactory
   * @param {import('./handshake.js').ClientBinding<TProxy>} binding Supplied by generated code.
   */
  constructor(config, nexusFactory, binding) {
    this.config = config;
    this._nexusFactory = nexusFactory;
    this._binding = binding;
  }

  /** @returns {number} */
  get maxConnections() { throw notImplemented('NexusClientPool.maxConnections'); }

  /** @returns {number} */
  get availableConnections() { throw notImplemented('NexusClientPool.availableConnections'); }

  /**
   * Rents a connected client, connecting a new one when none is idle and the pool is not full.
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<RentedNexusClient<TProxy>>}
   */
  rent(options) { throw notImplemented('NexusClientPool.rent'); }

  /**
   * Returns a connector that resolves a collection on a pooled client.
   * @template T
   * @param {(proxy: TProxy) => import('./collections.js').NexusList<T>} selector
   * @returns {{ getCollection(): Promise<import('./collections.js').NexusList<T>> }}
   */
  collectionConnector(selector) { throw notImplemented('NexusClientPool.collectionConnector'); }

  /** Disconnects every client. @returns {Promise<void>} */
  [Symbol.asyncDispose]() { throw notImplemented('NexusClientPool[Symbol.asyncDispose]'); }
}
