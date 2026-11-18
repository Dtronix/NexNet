/**
 * Base class of generated client nexuses. J07 implements it in `src/client/ClientNexusBase.js`.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * What a client nexus method can reach while connected. Mirrors C# `ClientContext<TProxy>`.
 *
 * @template {import('./proxy.js').ProxyBase} TProxy
 * @typedef {object} ClientContext
 * @property {TProxy} proxy The server proxy.
 * @property {bigint | null} sessionId The session ID, or null before the handshake completes.
 * @property {() => import('./pipes.js').NexusDuplexPipe} createPipe
 * @property {<T>(formatter: import('./codec.js').Formatter<T>) => import('./pipes.js').NexusDuplexChannel<T>} createChannel
 * @property {() => Promise<void>} disconnect
 */

/**
 * Per-invocation state the runtime passes to {@link ClientNexusBase#_dispatch}.
 *
 * @typedef {object} DispatchContext
 * @property {number} invocationId
 * @property {AbortSignal} signal Aborted when the server cancels the invocation or the session ends.
 * @property {(formatter: import('./codec.js').Formatter<any>, value: unknown) => void} writeResult Sets the result
 *   of a `ValueTask<T>` method. Not called for `void`/`ValueTask` methods.
 * @property {(pipeId: number) => import('./pipes.js').NexusDuplexPipe} pipe The pipe for a pipe parameter.
 * @property {<T>(pipeId: number, formatter: import('./codec.js').Formatter<T>) => import('./pipes.js').NexusDuplexChannel<T>} channel
 *   The channel for a channel parameter.
 */

/**
 * Base class of a generated `<Client>NexusBase`. Users extend the generated class and implement its methods.
 *
 * @template {import('./proxy.js').ProxyBase} [TProxy=import('./proxy.js').ProxyBase]
 */
export class ClientNexusBase {
  /**
   * The connection context. Throws when the nexus is not attached to a client.
   * @returns {ClientContext<TProxy>}
   */
  get context() { throw notImplemented('ClientNexusBase.context'); }

  /**
   * Called after the handshake completes.
   * @param {boolean} isReconnected True after a reconnect.
   * @returns {void | Promise<void>}
   */
  onConnected(isReconnected) {}

  /**
   * Called when the session ends.
   * @param {import('./enums.js').DisconnectReasonValue} reason
   * @returns {void | Promise<void>}
   */
  onDisconnected(reason) {}

  /**
   * Called before each reconnect attempt.
   * @returns {void | Promise<void>}
   */
  onReconnecting() {}

  /**
   * Dispatches one server-to-client invocation. Generated code overrides it with a `switch (methodId)` that reads
   * the arguments array from `reader`, calls the method, and reports the result through `ctx.writeResult`.
   * Unknown IDs fall through to this base implementation, which throws. The runtime has already checked
   * `methodId` against `acceptedIds`.
   *
   * @protected
   * @param {number} methodId
   * @param {import('./codec.js').MsgPackReader} reader Positioned at the arguments array.
   * @param {DispatchContext} ctx
   * @returns {void | Promise<void>}
   */
  _dispatch(methodId, reader, ctx) { throw notImplemented('ClientNexusBase._dispatch'); }
}
