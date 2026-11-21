/**
 * Base class of generated server proxies. Contract: `src/contract/proxy.js`.
 * @module
 */
export type ProxySession = object;
export type ArgumentsWriter = (writer: import('../contract/codec.js').MsgPackWriter) => void;
export type PipeArgumentsWriter = (writer: import('../contract/codec.js').MsgPackWriter, pipeId: number) => void;
export type InvokeOptions<T> = {
    /**
     * Cancels the call (`CancellationToken`). A cancel before send rejects with
     * `AbortError`; after send the call rejects at once and the late result is consumed.
     */
    signal?: AbortSignal;
    /**
     * Result formatter; omit for `ValueTask` (no result).
     */
    returns?: import('../contract/codec.js').Formatter<T>;
};
export type SessionHandle = {
    readonly client: {
        readonly config: import('../contract/config.js').ClientConfig;
    };
    readonly session: import('../session/NexusSession.js').NexusSession | null;
};
/**
 * Opaque session handle the runtime passes to proxy constructors. Generated code never touches it.
 *
 * @typedef {object} ProxySession
 */
/**
 * Writes the invocation arguments: one MessagePack array with an element per C# parameter, in declaration order,
 * excluding the `CancellationToken`.
 *
 * @callback ArgumentsWriter
 * @param {import('../contract/codec.js').MsgPackWriter} writer
 * @returns {void}
 */
/**
 * Writes the arguments of a pipe or channel method. The pipe or channel parameter is written as `pipeId`
 * (a uint) at its position.
 *
 * @callback PipeArgumentsWriter
 * @param {import('../contract/codec.js').MsgPackWriter} writer
 * @param {number} pipeId
 * @returns {void}
 */
/**
 * @template T
 * @typedef {object} InvokeOptions
 * @property {AbortSignal} [signal] Cancels the call (`CancellationToken`). A cancel before send rejects with
 *   `AbortError`; after send the call rejects at once and the late result is consumed.
 * @property {import('../contract/codec.js').Formatter<T>} [returns] Result formatter; omit for `ValueTask` (no result).
 */
/**
 * The handle the client passes: it follows the client's current session.
 * @typedef {{ readonly client: { readonly config: import('../contract/config.js').ClientConfig }, readonly session: import('../session/NexusSession.js').NexusSession | null }} SessionHandle
 */
/**
 * Base class of a generated `<Server>Proxy`. Generated methods call the protected `_invoke*` members.
 */
export declare class ProxyBase {
    /**
     * @protected
     * @readonly
     * @type {ProxySession}
     */
    protected readonly _session: ProxySession;
    /**
     * @param {ProxySession} session
     */
    constructor(session: ProxySession);
    /**
     * Invokes a method that returns `ValueTask` or `ValueTask<T>`.
     *
     * @template [T=void]
     * @protected
     * @param {number} methodId
     * @param {ArgumentsWriter | null} args Null when the method has no parameters.
     * @param {InvokeOptions<T>} [options]
     * @returns {Promise<T>}
     */
    protected _invoke<T = void>(methodId: number, args: ArgumentsWriter | null, options?: InvokeOptions<T>): Promise<T>;
    /**
     * Invokes a `void` (fire-and-forget) method. Waits for invocation credit internally; never drops the call.
     * Failures are logged, not thrown.
     *
     * @protected
     * @param {number} methodId
     * @param {ArgumentsWriter | null} args
     * @returns {void}
     */
    protected _invokeVoid(methodId: number, args: ArgumentsWriter | null): void;
    /**
     * Invokes a method with a `INexusDuplexPipe` parameter (`IgnoreReturn | DuplexPipe`). Resolves once sent;
     * use `pipe.ready`/`pipe.completed` for the pipe lifetime.
     *
     * @protected
     * @param {number} methodId
     * @param {import('../contract/pipes.js').NexusDuplexPipe} pipe A pipe from `client.createPipe()`.
     * @param {PipeArgumentsWriter} args
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    protected _invokePipe(methodId: number, pipe: import('../contract/pipes.js').NexusDuplexPipe, args: PipeArgumentsWriter, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Invokes a method with a channel parameter (`INexusDuplexChannel<T>`, `INexusDuplexUnmanagedChannel<T>`).
     *
     * @protected
     * @param {number} methodId
     * @param {import('../contract/pipes.js').NexusDuplexChannel<any>} channel A channel from `client.createChannel(formatter)`.
     * @param {PipeArgumentsWriter} args
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    protected _invokeChannel(methodId: number, channel: import('../contract/pipes.js').NexusDuplexChannel<any>, args: PipeArgumentsWriter, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Returns the synchronized collection behind a server nexus collection property. The same instance is returned
     * for the lifetime of the client; it follows the client's current session.
     *
     * @template T
     * @protected
     * @param {number} methodId The collection's method ID.
     * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
     * @param {import('../collections/NexusList.js').NexusListOptions<T>} [options] `mode` (the `[NexusCollection]`
     *   mode, default ServerToClient), `equals` (item equality for `remove`/`indexOf`/`contains`; default: an
     *   item's own `equals` method, else SameValueZero) and `valuesFormatter` (the C# `T[]` formatter, for primitive
     *   arrays written as ext 78).
     * @returns {import('../contract/collections.js').NexusList<T>}
     */
    protected _collection<T>(methodId: number, formatter: import('../contract/codec.js').Formatter<T>, options?: import('../collections/NexusList.js').NexusListOptions<T>): import('../contract/collections.js').NexusList<T>;
}
