/**
 * The client. J05 implements it in `src/client/NexusClient.js`.
 * @module
 */

import { notImplemented } from './internal.js';
import { unimplementedTransport } from './transport.js';

/**
 * Result of {@link NexusClient#tryConnect}. Mirrors C# `ConnectionResult`.
 *
 * @typedef {object} ConnectionResult
 * @property {boolean} success
 * @property {import('./enums.js').ConnectionResultStateValue} state
 * @property {import('./enums.js').DisconnectReasonValue} disconnectReason `None` on success.
 * @property {unknown} [error] The error when `state` is `Exception`.
 */

/**
 * A NexNet client: one session to one server. Dispatches `statechange` and `disconnected` events.
 * Create it with the generated `create<Export>Client(config, nexus)`, which calls {@link NexusClient.create}.
 *
 * @template {import('./nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('./proxy.js').ProxyBase} TProxy
 */
export class NexusClient extends EventTarget {
  /**
   * @readonly
   * @type {import('./config.js').ClientConfig}
   */
  config;

  /**
   * @readonly
   * @type {TNexus}
   */
  nexus;

  /**
   * @protected
   * @readonly
   * @type {import('./handshake.js').ClientBinding<TProxy>}
   */
  _binding;

  /**
   * Transport factories this entry point supports, keyed by {@link ClientConfig} transport property.
   * The browser entry has `webSocket` only; the Node entry's `NexusClient` adds the rest.
   *
   * @type {Readonly<Partial<Record<import('./config.js').TransportKind, import('./transport.js').TransportFactory>>>}
   */
  static transports = Object.freeze({
    webSocket: unimplementedTransport('webSocket'),
  });

  /**
   * Creates a client of the class it is called on. Validates `config` and throws `TypeError` when it is invalid
   * (no transport, several transports, or a transport this entry point does not support).
   *
   * @template {import('./nexus.js').ClientNexusBase<TP>} TN
   * @template {import('./proxy.js').ProxyBase} TP
   * @param {import('./config.js').ClientConfig} config
   * @param {TN} nexus
   * @param {import('./handshake.js').ClientBinding<TP>} binding Supplied by the generated factory.
   * @returns {NexusClient<TN, TP>}
   */
  static create(config, nexus, binding) {
    return new this(config, nexus, binding);
  }

  /**
   * Use {@link NexusClient.create}.
   *
   * @param {import('./config.js').ClientConfig} config
   * @param {TNexus} nexus
   * @param {import('./handshake.js').ClientBinding<TProxy>} binding
   */
  constructor(config, nexus, binding) {
    super();
    this.config = config;
    this.nexus = nexus;
    this._binding = binding;
  }

  /** @returns {import('./enums.js').ConnectionStateValue} */
  get state() { throw notImplemented('NexusClient.state'); }

  /** The server proxy. Throws before the first successful connect. @returns {TProxy} */
  get proxy() { throw notImplemented('NexusClient.proxy'); }

  /** True when the transport is TLS, WSS, HTTPS or UDS (D7). @returns {boolean} */
  get isSecure() { throw notImplemented('NexusClient.isSecure'); }

  /** Resolves when the current session ends (after reconnection gives up). @returns {Promise<void>} */
  get disconnected() { throw notImplemented('NexusClient.disconnected'); }

  /**
   * Connects and completes the handshake. Rejects with `NexusTransportError` on failure and the abort reason
   * when `signal` aborts.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  connect(options) { throw notImplemented('NexusClient.connect'); }

  /**
   * Like {@link NexusClient#connect}, but resolves a result instead of rejecting.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<ConnectionResult>}
   */
  tryConnect(options) { throw notImplemented('NexusClient.tryConnect'); }

  /** Disconnects gracefully and stops reconnection. @returns {Promise<void>} */
  disconnect() { throw notImplemented('NexusClient.disconnect'); }

  /** Creates a pipe to pass to a pipe method. @returns {import('./pipes.js').NexusDuplexPipe} */
  createPipe() { throw notImplemented('NexusClient.createPipe'); }

  /**
   * Creates a channel to pass to a channel method.
   * @template T
   * @param {import('./codec.js').Formatter<T>} formatter
   * @returns {import('./pipes.js').NexusDuplexChannel<T>}
   */
  createChannel(formatter) { throw notImplemented('NexusClient.createChannel'); }

  /** Diagnostic snapshot, field names as C# `NexusClientSnapshot` camelCased; null when not connected. @returns {object | null} */
  getSnapshot() { throw notImplemented('NexusClient.getSnapshot'); }

  /** Same as {@link NexusClient#disconnect}. @returns {Promise<void>} */
  [Symbol.asyncDispose]() { throw notImplemented('NexusClient[Symbol.asyncDispose]'); }
}
