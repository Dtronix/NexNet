/**
 * Base class of generated client nexuses (J07). Contract: `src/contract/nexus.js`.
 * @module
 */
export type ClientContext<TProxy extends import('./ProxyBase.js').ProxyBase> = {
    /**
     * The server proxy.
     */
    proxy: TProxy;
    /**
     * The session ID, or null before the handshake completes.
     */
    sessionId: bigint | null;
    createPipe: () => import('../contract/pipes.js').NexusDuplexPipe;
    createChannel: <T>(formatter: import('../contract/codec.js').Formatter<T>) => import('../contract/pipes.js').NexusDuplexChannel<T>;
    disconnect: () => Promise<void>;
};
export type DispatchContext = {
    invocationId: number;
    /**
     * Aborted when the server cancels the invocation or the session ends.
     */
    signal: AbortSignal;
    /**
     * Sets the result
     * of a `ValueTask<T>` method. Not called for `void`/`ValueTask` methods.
     */
    writeResult: (formatter: import('../contract/codec.js').Formatter<any>, value: unknown) => void;
    /**
     * The pipe for a pipe parameter.
     */
    pipe: (pipeId: number) => import('../contract/pipes.js').NexusDuplexPipe;
    /**
     *   The channel for a channel parameter.
     */
    channel: <T>(pipeId: number, formatter: import('../contract/codec.js').Formatter<T>) => import('../contract/pipes.js').NexusDuplexChannel<T>;
};
export type NexusRemoteError = {
    /**
     * Application error code (int32), or null.
     */
    code?: number | null;
    /**
     * Message for the caller (truncated to 2048 characters), or null.
     */
    message?: string | null;
};
export type AttachedClient = {
    proxy: any;
    _session: {
        id: bigint | null;
    } | null;
    createPipe: () => import('../contract/pipes.js').NexusDuplexPipe;
    createChannel: <T>(formatter: import('../contract/codec.js').Formatter<T>) => import('../contract/pipes.js').NexusDuplexChannel<T>;
    disconnect: () => Promise<void>;
};
/**
 * Attaches `nexus` to `client`, so `nexus.context` works. Called by the client constructor.
 * @param {object} nexus
 * @param {AttachedClient} client
 * @returns {void}
 */
export declare function bindNexusClient(nexus: object, client: AttachedClient): void;
/**
 * Base class of a generated `<Client>NexusBase`. Users extend the generated class and implement its methods.
 *
 * @template {import('./ProxyBase.js').ProxyBase} [TProxy=import('./ProxyBase.js').ProxyBase]
 */
export declare class ClientNexusBase<TProxy extends import('./ProxyBase.js').ProxyBase = import('./ProxyBase.js').ProxyBase> {
    /**
     * The connection context. Throws when the nexus is not attached to a client.
     * @returns {ClientContext<TProxy>}
     */
    get context(): ClientContext<TProxy>;
    /**
     * Called after the handshake completes.
     * @param {boolean} isReconnected True after a reconnect.
     * @returns {void | Promise<void>}
     */
    onConnected(isReconnected: boolean): void | Promise<void>;
    /**
     * Called when the session ends.
     * @param {import('../contract/enums.js').DisconnectReasonValue} reason
     * @returns {void | Promise<void>}
     */
    onDisconnected(reason: import('../contract/enums.js').DisconnectReasonValue): void | Promise<void>;
    /**
     * Called before each reconnect attempt.
     * @returns {void | Promise<void>}
     */
    onReconnecting(): void | Promise<void>;
    /**
     * Called when a method that returns a result throws, before the failure is sent to the server. Return an
     * application error to send in place of the exception's detail, whatever `remoteExceptionDetail` says; return null
     * to send what that setting selects. Not called for canceled or fire-and-forget invocations. Mirrors C#
     * `OnInvocationException`.
     *
     * @param {unknown} error The error the method threw.
     * @param {number} methodId
     * @returns {NexusRemoteError | null | undefined}
     */
    onInvocationException(error: unknown, methodId: number): NexusRemoteError | null | undefined;
    /**
     * True for methods that run in the separate invocation pool (`[NexusMethod(SeparatePool = true)]`), limited by
     * `maxSeparatePoolInvocations` instead of `maxConcurrentConnectionInvocations`. Generated code overrides it when
     * the nexus has such methods.
     *
     * @protected
     * @param {number} methodId
     * @returns {boolean}
     */
    protected _separatePool(methodId: number): boolean;
    /**
     * Dispatches one server-to-client invocation. Generated code overrides it with a `switch (methodId)` that reads
     * the arguments array from `reader`, calls the method, and reports the result through `ctx.writeResult`.
     * Unknown IDs fall through to this base implementation, which throws. The runtime has already checked
     * `methodId` against `acceptedIds`.
     *
     * @protected
     * @param {number} methodId
     * @param {import('../contract/codec.js').MsgPackReader} reader Positioned at the arguments array.
     * @param {DispatchContext} ctx
     * @returns {void | Promise<void>}
     */
    protected _dispatch(methodId: number, reader: import('../contract/codec.js').MsgPackReader, ctx: DispatchContext): void | Promise<void>;
}
