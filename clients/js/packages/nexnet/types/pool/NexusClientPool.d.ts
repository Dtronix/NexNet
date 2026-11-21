/**
 * Client pool. Mirrors C# `NexusClientPool`: up to `maxConnections` clients, rented one caller at a time and
 * returned by releasing the rental; idle clients beyond `minIdleConnections` are disconnected after `maxIdleTime`, and
 * unhealthy (disconnected) ones are dropped by a periodic health check.
 * @module
 */
import { NexusError } from '../contract/errors.js';
import { NexusClient } from '../client/NexusClient.js';
export type PoolConfig = import('../contract/pool.js').NexusClientPoolConfig & {
    clientClass?: typeof NexusClient;
};
export type RentedClient<TProxy extends import('../contract/proxy.js').ProxyBase> = import('../contract/pool.js').RentedNexusClient<TProxy> & {
    [Symbol.dispose](): void;
};
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
export declare class NexusClientPoolConnectionError extends NexusError {
    /** The failed connection result. */
    result: import("../client/NexusClient.js").ConnectionResult;
    /**
     * @param {string} message
     * @param {import('../client/NexusClient.js').ConnectionResult} result
     */
    constructor(message: string, result: import('../client/NexusClient.js').ConnectionResult);
}
export type PooledClient = {
    client: NexusClient<any, any>;
    /**
     * Clock time of the last rent or return.
     */
    lastUsed: number;
    disposed: boolean;
};
/**
 * Pool of connected clients to one server.
 *
 * @template {import('../contract/nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('../contract/proxy.js').ProxyBase} TProxy
 */
export declare class NexusClientPool<TNexus extends import('../contract/nexus.js').ClientNexusBase<TProxy>, TProxy extends import('../contract/proxy.js').ProxyBase> {
    #private;
    /**
     * The client class pooled clients are created with when `config.clientClass` is not set.
     * @type {typeof NexusClient}
     */
    static clientClass: typeof NexusClient;
    /**
     * @readonly
     * @type {import('../contract/pool.js').NexusClientPoolConfig}
     */
    readonly config: import('../contract/pool.js').NexusClientPoolConfig;
    /**
     * @protected
     * @readonly
     * @type {() => TNexus}
     */
    protected readonly _nexusFactory: () => TNexus;
    /**
     * @protected
     * @readonly
     * @type {import('../contract/handshake.js').ClientBinding<TProxy>}
     */
    protected readonly _binding: import('../contract/handshake.js').ClientBinding<TProxy>;
    /**
     * @param {import('../contract/pool.js').NexusClientPoolConfig} config
     * @param {() => TNexus} nexusFactory
     * @param {import('../contract/handshake.js').ClientBinding<TProxy>} binding Supplied by generated code.
     */
    constructor(config: import('../contract/pool.js').NexusClientPoolConfig, nexusFactory: () => TNexus, binding: import('../contract/handshake.js').ClientBinding<TProxy>);
    /** @returns {number} */
    get maxConnections(): number;
    /** Idle clients ready to rent. @returns {number} */
    get availableConnections(): number;
    /**
     * Rents a connected client, connecting a new one when none is idle and the pool is not full. Waits in FIFO order
     * while `maxConnections` clients are rented. Rejects with the abort reason when `signal` aborts, and with
     * {@link NexusClientPoolConnectionError} when a new client fails to connect.
     *
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<RentedClient<TProxy>>}
     */
    rent(options?: {
        signal?: AbortSignal;
    }): Promise<RentedClient<TProxy>>;
    /**
     * Returns a connector that resolves a collection on a pooled client. Each `getCollection()` rents a client;
     * `release()` (or disposing the connector) returns every client it rented.
     * @template T
     * @param {(proxy: TProxy) => import('../contract/collections.js').NexusList<T>} selector
     * @returns {{ getCollection(): Promise<import('../contract/collections.js').NexusList<T>>, release(): void, [Symbol.dispose](): void }}
     */
    collectionConnector<T>(selector: (proxy: TProxy) => import('../contract/collections.js').NexusList<T>): {
        getCollection(): Promise<import('../contract/collections.js').NexusList<T>>;
        release(): void;
        [Symbol.dispose](): void;
    };
    /** Disconnects every idle client and stops the health check. Rented clients are disconnected when released. @returns {Promise<void>} */
    [Symbol.asyncDispose](): Promise<void>;
}
