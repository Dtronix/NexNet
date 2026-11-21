/**
 * Base class of generated client nexuses. Contract: `src/contract/nexus.js`.
 * @module
 */

import { notImplemented } from '../contract/internal.js';

/**
 * `createChannel` of a client and of a client context: a formatter typed as reading `T | null` (a generated class
 * formatter, `stringFormatter`) gives a `NexusDuplexChannel<T>`, the type a channel parameter of a generated proxy takes.
 *
 * @typedef {{
 *   <T>(formatter: import('../contract/codec.js').Formatter<T | null>): import('../contract/pipes.js').NexusDuplexChannel<T>;
 *   <T>(formatter: import('../contract/codec.js').Formatter<T>): import('../contract/pipes.js').NexusDuplexChannel<T>;
 * }} CreateChannel
 */

/**
 * What a client nexus method can reach while connected. Mirrors C# `ClientContext<TProxy>`.
 *
 * @template {import('./ProxyBase.js').ProxyBase} TProxy
 * @typedef {object} ClientContext
 * @property {TProxy} proxy The server proxy.
 * @property {bigint | null} sessionId The session ID, or null before the handshake completes.
 * @property {boolean} isSecure True when the transport encrypts the connection (`NexusClient.isSecure`).
 * @property {string | null} remoteAddress The server's address (`NexusClient.remoteAddress`), or null.
 * @property {number | null} remotePort The server's port (`NexusClient.remotePort`), or null.
 * @property {() => import('../contract/pipes.js').NexusDuplexPipe} createPipe
 * @property {CreateChannel} createChannel
 * @property {() => Promise<void>} disconnect
 */

/**
 * Per-invocation state the runtime passes to {@link ClientNexusBase#_dispatch}.
 *
 * @typedef {object} DispatchContext
 * @property {number} invocationId
 * @property {AbortSignal} signal Aborted when the server cancels the invocation or the session ends.
 * @property {(formatter: import('../contract/codec.js').Formatter<any>, value: unknown) => void} writeResult Sets the result
 *   of a `ValueTask<T>` method. Not called for `void`/`ValueTask` methods.
 * @property {(pipeId: number) => import('../contract/pipes.js').NexusDuplexPipe} pipe The pipe for a pipe parameter.
 * @property {<T>(pipeId: number, formatter: import('../contract/codec.js').Formatter<T>) => import('../contract/pipes.js').NexusDuplexChannel<T>} channel
 *   The channel for a channel parameter.
 */

/**
 * The application error `onInvocationException` may return in place of the exception's detail. Mirrors C#
 * `NexusRemoteError`.
 *
 * @typedef {object} NexusRemoteError
 * @property {number | null} [code] Application error code (int32), or null.
 * @property {string | null} [message] Message for the caller (truncated to 2048 characters), or null.
 */

/**
 * The client a nexus is attached to, as the context needs it.
 * @typedef {object} AttachedClient
 * @property {any} proxy
 * @property {{ id: bigint | null } | null} _session
 * @property {boolean} isSecure
 * @property {string | null} remoteAddress
 * @property {number | null} remotePort
 * @property {() => import('../contract/pipes.js').NexusDuplexPipe} createPipe
 * @property {CreateChannel} createChannel
 * @property {() => Promise<void>} disconnect
 */

/** @type {WeakMap<object, ClientContext<any>>} */
const contexts = new WeakMap();

/**
 * Attaches `nexus` to `client`, so `nexus.context` works. Called by the client constructor.
 * @param {object} nexus
 * @param {AttachedClient} client
 * @returns {void}
 */
export function bindNexusClient(nexus, client) {
  contexts.set(nexus, Object.freeze({
    get proxy() {
      return client.proxy;
    },
    get sessionId() {
      return client._session?.id ?? null;
    },
    get isSecure() {
      return client.isSecure;
    },
    get remoteAddress() {
      return client.remoteAddress;
    },
    get remotePort() {
      return client.remotePort;
    },
    createPipe: () => client.createPipe(),
    createChannel: /** @type {CreateChannel} */ (
      /** @param {import('../contract/codec.js').Formatter<any>} formatter */ (formatter) => client.createChannel(formatter)),
    disconnect: () => client.disconnect(),
  }));
}

/**
 * Base class of a generated `<Client>NexusBase`. Users extend the generated class and implement its methods.
 *
 * @template {import('./ProxyBase.js').ProxyBase} [TProxy=import('./ProxyBase.js').ProxyBase]
 */
export class ClientNexusBase {
  /**
   * The connection context. Throws when the nexus is not attached to a client.
   * @returns {ClientContext<TProxy>}
   */
  get context() {
    const context = contexts.get(this);
    if (!context) throw new Error('The nexus is not attached to a client.');
    return context;
  }

  /**
   * Called after the handshake completes.
   * @param {boolean} isReconnected True after a reconnect.
   * @returns {void | Promise<void>}
   */
  onConnected(isReconnected) {}

  /**
   * Called when the session ends.
   * @param {import('../contract/enums.js').DisconnectReasonValue} reason
   * @returns {void | Promise<void>}
   */
  onDisconnected(reason) {}

  /**
   * Called before each reconnect attempt.
   * @returns {void | Promise<void>}
   */
  onReconnecting() {}

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
  onInvocationException(error, methodId) {
    return null;
  }

  /**
   * True for methods that run in the separate invocation pool (`[NexusMethod(SeparatePool = true)]`), limited by
   * `maxSeparatePoolInvocations` instead of `maxConcurrentConnectionInvocations`. Generated code overrides it when
   * the nexus has such methods.
   *
   * @protected
   * @param {number} methodId
   * @returns {boolean}
   */
  _separatePool(methodId) {
    return false;
  }

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
  _dispatch(methodId, reader, ctx) {
    throw notImplemented(`ClientNexusBase._dispatch (method ${methodId})`);
  }
}
