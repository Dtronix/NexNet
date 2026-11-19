/**
 * Base class of generated server proxies (J07). Contract: `src/contract/proxy.js`.
 * @module
 */

import { NexusError, NexusTransportError, cancellationError } from '../contract/errors.js';
import { notImplemented } from '../contract/internal.js';
import { serialize, invocationCodec } from '../invocation/codec.js';
import { getInvocationManager } from '../invocation/InvocationManager.js';

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
  async _invoke(methodId, args, options) {
    const signal = options?.signal;
    if (signal?.aborted) throw cancellationError(signal);
    const manager = connectedManager(this._session);
    const value = await manager.invoke(methodId, args ? serialize(args) : null, { signal, expectResult: true });
    const returns = options?.returns;
    if (!returns) return /** @type {T} */ (/** @type {unknown} */ (undefined));
    if (value === null) throw new NexusError(`The result of method ${methodId} carried no value.`);
    return returns.read(invocationCodec().createReader(value, manager.serializerOptions));
  }

  /**
   * Invokes a `void` (fire-and-forget) method. Waits for invocation credit internally; never drops the call.
   * Failures are logged, not thrown.
   *
   * @protected
   * @param {number} methodId
   * @param {ArgumentsWriter | null} args
   * @returns {void}
   */
  _invokeVoid(methodId, args) {
    const handle = /** @type {SessionHandle} */ (this._session);
    try {
      const manager = connectedManager(handle);
      manager.invoke(methodId, args ? serialize(args) : null, { expectResult: false })
        .catch((error) => logFailure(handle, methodId, error));
    } catch (error) {
      logFailure(handle, methodId, error);
    }
  }

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
  _invokePipe(methodId, pipe, args, options) { throw notImplemented('ProxyBase._invokePipe'); }

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
  _invokeChannel(methodId, channel, args, options) { throw notImplemented('ProxyBase._invokeChannel'); }

  /**
   * Returns the synchronized collection behind a server nexus collection property. The same instance is returned
   * for the lifetime of the client.
   *
   * @template T
   * @protected
   * @param {number} methodId The collection's method ID.
   * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
   * @param {{ equals?: (a: T, b: T) => boolean }} [options] Item equality for `remove`/`indexOf`; default `Object.is`.
   * @returns {import('../contract/collections.js').NexusList<T>}
   */
  _collection(methodId, formatter, options) { throw notImplemented('ProxyBase._collection'); }
}

/**
 * The invocation manager of the handle's current session; throws `NexusTransportError` when not connected.
 * @param {ProxySession} handle
 * @returns {import('../invocation/InvocationManager.js').InvocationManager}
 */
function connectedManager(handle) {
  const session = /** @type {SessionHandle} */ (handle).session;
  const manager = getInvocationManager(session);
  if (manager === null || !manager.isConnected) {
    throw new NexusTransportError('Session is not connected.', { reason: session?.disconnectReason ?? 0 });
  }
  return manager;
}

/**
 * @param {SessionHandle} handle
 * @param {number} methodId
 * @param {unknown} error
 */
function logFailure(handle, methodId, error) {
  const logger = handle.client.config.logger;
  if (!logger) return;
  try {
    logger.log('warning', `Fire-and-forget invocation of method ${methodId} failed.`, error);
  } catch {
    // A logger must never break the caller.
  }
}
