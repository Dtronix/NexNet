/**
 * Base class of generated server proxies. J07 implements it in `src/client/ProxyBase.js`.
 * @module
 */

import { notImplemented } from './internal.js';

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
 * @param {import('./codec.js').MsgPackWriter} writer
 * @returns {void}
 */

/**
 * Writes the arguments of a pipe or channel method. The pipe or channel parameter is written as `pipeId`
 * (a uint) at its position.
 *
 * @callback PipeArgumentsWriter
 * @param {import('./codec.js').MsgPackWriter} writer
 * @param {number} pipeId
 * @returns {void}
 */

/**
 * @template T
 * @typedef {object} InvokeOptions
 * @property {AbortSignal} [signal] Cancels the call (`CancellationToken`). A cancel before send rejects with
 *   `AbortError`; after send the call rejects at once and the late result is consumed.
 * @property {import('./codec.js').Formatter<T>} [returns] Result formatter; omit for `ValueTask` (no result).
 */

/**
 * Base class of a generated `<Server>Proxy`. Generated methods call the protected `_invoke*` members.
 */
export class ProxyBase {
  /**
   * @protected
   * @readonly
   * @type {ProxySession}
   */
  _session;

  /**
   * @param {ProxySession} session
   */
  constructor(session) {
    this._session = session;
  }

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
  _invoke(methodId, args, options) { throw notImplemented('ProxyBase._invoke'); }

  /**
   * Invokes a `void` (fire-and-forget) method. Waits for invocation credit internally; never drops the call.
   * Failures are logged, not thrown.
   *
   * @protected
   * @param {number} methodId
   * @param {ArgumentsWriter | null} args
   * @returns {void}
   */
  _invokeVoid(methodId, args) { throw notImplemented('ProxyBase._invokeVoid'); }

  /**
   * Invokes a method with a `INexusDuplexPipe` parameter (`IgnoreReturn | DuplexPipe`). Resolves once sent;
   * use `pipe.ready`/`pipe.completed` for the pipe lifetime.
   *
   * @protected
   * @param {number} methodId
   * @param {import('./pipes.js').NexusDuplexPipe} pipe A pipe from `client.createPipe()`.
   * @param {PipeArgumentsWriter} args
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  _invokePipe(methodId, pipe, args, options) { throw notImplemented('ProxyBase._invokePipe'); }

  /**
   * Invokes a method with a channel parameter (`INexusDuplexChannel<T>`, `INexusDuplexUnmanagedChannel<T>`).
   *
   * @protected
   * @param {number} methodId
   * @param {import('./pipes.js').NexusDuplexChannel<any>} channel A channel from `client.createChannel(formatter)`.
   * @param {PipeArgumentsWriter} args
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  _invokeChannel(methodId, channel, args, options) { throw notImplemented('ProxyBase._invokeChannel'); }

  /**
   * Returns the synchronized collection behind a server nexus collection property. The same instance is returned
   * for the lifetime of the client.
   *
   * @template T
   * @protected
   * @param {number} methodId The collection's method ID.
   * @param {import('./codec.js').Formatter<T>} formatter Item formatter.
   * @param {{ equals?: (a: T, b: T) => boolean }} [options] Item equality for `remove`/`indexOf`; default `Object.is`.
   * @returns {import('./collections.js').NexusList<T>}
   */
  _collection(methodId, formatter, options) { throw notImplemented('ProxyBase._collection'); }
}
