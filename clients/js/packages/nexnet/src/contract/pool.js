/**
 * Client pool (D20). Implemented in `src/pool/` (J10); this file re-exports it. Mirrors C# `NexusClientPool`.
 * @module
 */

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

export { NexusClientPool } from '../pool/NexusClientPool.js';
