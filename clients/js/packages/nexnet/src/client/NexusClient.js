/**
 * The client (J05): connect, handshake, state machine, events, plaintext warning and disconnect.
 * Reconnection, pooling and full snapshots are J10. Invocations (J07) and pipes (J08) attach to each session before it
 * starts (`attachSessionLayers`).
 * @module
 */

import { ConnectionResultState, ConnectionState, DisconnectReason } from '../contract/enums.js';
import { NexusTransportError, cancellationError } from '../contract/errors.js';
import { webSocketTransport } from '../transport/WebSocketTransport.js';
import { NexusSession } from '../session/NexusSession.js';
import { resolveClientOptions } from '../session/options.js';
import { MAX_VERSION_LENGTH } from '../session/protocol.js';
import { attachInvocations } from '../invocation/InvocationManager.js';
import { attachPipes, createChannel, createPipe } from '../pipes/PipeManager.js';
import { bindNexusClient } from './ClientNexusBase.js';

/**
 * Result of {@link NexusClient#tryConnect}. Mirrors C# `ConnectionResult`.
 *
 * @typedef {object} ConnectionResult
 * @property {boolean} success
 * @property {import('../contract/enums.js').ConnectionResultStateValue} state
 * @property {import('../contract/enums.js').DisconnectReasonValue} disconnectReason `None` on success.
 * @property {unknown} [error] The error when `state` is `Exception`.
 */

/**
 * `detail` of the `statechange` event.
 * @typedef {{ state: import('../contract/enums.js').ConnectionStateValue, previous: import('../contract/enums.js').ConnectionStateValue }} StateChangeDetail
 */

/**
 * `detail` of the `disconnected` event.
 * @typedef {{ reason: import('../contract/enums.js').DisconnectReasonValue }} DisconnectedDetail
 */

/**
 * The opaque session handle passed to the generated proxy. It follows the client's current session.
 * @typedef {{ readonly client: NexusClient<any, any>, readonly session: NexusSession | null }} ClientSessionHandle
 */

/** @type {ConnectionResult} */
const SUCCESS = Object.freeze({ success: true, state: ConnectionResultState.Success, disconnectReason: DisconnectReason.None });

/**
 * A NexNet client: one session to one server. Dispatches `statechange` and `disconnected` events (`CustomEvent`
 * with {@link StateChangeDetail} and {@link DisconnectedDetail}), always from a microtask, never re-entrantly.
 * Create it with the generated `create<Export>Client(config, nexus)`, which calls {@link NexusClient.create}.
 *
 * @template {import('../contract/nexus.js').ClientNexusBase<TProxy>} TNexus
 * @template {import('../contract/proxy.js').ProxyBase} TProxy
 */
export class NexusClient extends EventTarget {
  /**
   * @readonly
   * @type {import('../contract/config.js').ClientConfig}
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
   * @type {import('../contract/handshake.js').ClientBinding<TProxy>}
   */
  _binding;

  /**
   * Transport factories this entry point supports, keyed by {@link ClientConfig} transport property.
   * The browser entry has `webSocket` only; the Node entry's `NexusClient` adds the rest.
   *
   * @type {Readonly<Partial<Record<import('../contract/config.js').TransportKind, import('../transport/ITransport.js').TransportFactory>>>}
   */
  static transports = Object.freeze({
    webSocket: webSocketTransport,
  });

  /** @type {import('../session/options.js').ResolvedClientOptions} */
  #options;
  /** @type {NexusSession | null} */
  #session = null;
  /** @type {import('../contract/enums.js').ConnectionStateValue} */
  #state = ConnectionState.Disconnected;
  /** @type {TProxy | null} */
  #proxy = null;
  /** @type {ClientSessionHandle} */
  #handle;
  #plaintextWarned = false;
  /** @type {Promise<ConnectionResult> | null} */
  #connecting = null;
  /** @type {Promise<void>} */
  #disconnected = Promise.resolve();

  /**
   * Creates a client of the class it is called on. Validates `config` and throws `TypeError` when it is invalid
   * (no transport, several transports, or a transport this entry point does not support).
   *
   * @template {import('../contract/nexus.js').ClientNexusBase<TP>} TN
   * @template {import('../contract/proxy.js').ProxyBase} TP
   * @param {import('../contract/config.js').ClientConfig} config
   * @param {TN} nexus
   * @param {import('../contract/handshake.js').ClientBinding<TP>} binding Supplied by the generated factory.
   * @returns {NexusClient<TN, TP>}
   */
  static create(config, nexus, binding) {
    return new this(config, nexus, binding);
  }

  /**
   * Use {@link NexusClient.create}.
   *
   * @param {import('../contract/config.js').ClientConfig} config
   * @param {TNexus} nexus
   * @param {import('../contract/handshake.js').ClientBinding<TProxy>} binding
   */
  constructor(config, nexus, binding) {
    super();
    const ctor = /** @type {typeof NexusClient} */ (this.constructor);
    this.#options = resolveClientOptions(config, ctor.transports);
    validateBinding(binding);
    this.config = config;
    this.nexus = nexus;
    this._binding = binding;
    bindNexusClient(nexus, this);
    const client = this;
    this.#handle = Object.freeze({
      client,
      get session() {
        return client.#session;
      },
    });
  }

  /** @returns {import('../contract/enums.js').ConnectionStateValue} */
  get state() {
    return this.#state;
  }

  /** The server proxy. Throws before the first successful connect. @returns {TProxy} */
  get proxy() {
    if (this.#proxy === null) throw new Error('Client not connected');
    return this.#proxy;
  }

  /** True when the transport is TLS, WSS, HTTPS or UDS (D7). @returns {boolean} */
  get isSecure() {
    return this.#options.isSecure;
  }

  /** Resolves when the current session ends (after reconnection gives up). @returns {Promise<void>} */
  get disconnected() {
    return this.#disconnected;
  }

  /**
   * The current session, or null. Runtime-internal (invocations, pipes, reconnection).
   * @returns {NexusSession | null}
   */
  get _session() {
    return this.#session;
  }

  /**
   * Connects and completes the handshake. Rejects with `NexusTransportError` on failure and the abort reason
   * when `signal` aborts.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  async connect(options) {
    const result = await this.tryConnect(options);
    if (result.success) return;
    if (result.state === ConnectionResultState.Canceled) throw cancellationError(options?.signal);
    const reason = result.disconnectReason || DisconnectReason.SocketError;
    if (result.error instanceof NexusTransportError) throw result.error;
    const message = result.state === ConnectionResultState.Exception
      ? `Connection failed (${describeReason(reason)}).`
      : `Server disconnected during the handshake (${describeReason(reason)}).`;
    throw new NexusTransportError(message, { reason, cause: result.error });
  }

  /**
   * Like {@link NexusClient#connect}, but resolves a result instead of rejecting.
   *
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<ConnectionResult>}
   */
  tryConnect(options) {
    if (this.#session !== null) return Promise.resolve(SUCCESS);
    if (this.#connecting !== null) return this.#connecting;
    const attempt = this.#tryConnectCore(false, options?.signal);
    this.#connecting = attempt;
    void attempt.finally(() => {
      if (this.#connecting === attempt) this.#connecting = null;
    });
    return attempt;
  }

  /** Disconnects gracefully and stops reconnection. @returns {Promise<void>} */
  async disconnect() {
    const session = this.#session;
    if (session === null) return;
    await session.disconnect(DisconnectReason.Graceful);
    await this.#disconnected;
  }

  /**
   * Creates a pipe to pass to a pipe method. It reserves a pipe slot and receive window until it is disposed.
   * Throws `NexusTransportError` when not connected and `NexusBusyError` when `maxPipes` or the session budget is
   * reached.
   * @returns {import('../contract/pipes.js').NexusDuplexPipe}
   */
  createPipe() {
    return createPipe(this.#session);
  }

  /**
   * Creates a channel to pass to a channel method.
   * @template T
   * @param {import('../contract/codec.js').Formatter<T>} formatter
   * @returns {import('../contract/pipes.js').NexusDuplexChannel<T>}
   */
  createChannel(formatter) {
    return createChannel(this.#session, formatter);
  }

  /**
   * Diagnostic snapshot, field names as C# `NexusClientSnapshot` camelCased; null when not connected.
   * J05 fills the session fields; J10 adds the rest.
   * @returns {object | null}
   */
  getSnapshot() {
    const session = this.#session;
    if (session === null || !session.isHandshakeComplete) return null;
    return {
      id: session.id,
      state: session.state,
      isSecure: this.isSecure,
      transport: this.#options.transportName,
      maxMessageSize: this.#options.maxMessageSize,
      peerMaxMessageSize: session.peerMaxMessageSize,
      peerInvocationCredits: session.peerInvocationCredits,
      idleTimeoutMs: session.idleTimeoutMs,
      pingInterval: session.pingInterval,
      framesSent: session.framesSent,
      bytesSent: session.bytesSent,
      framesReceived: session.framesReceived,
      bytesReceived: session.bytesReceived,
      pendingWrites: session.pendingWrites,
    };
  }

  /** Same as {@link NexusClient#disconnect}. @returns {Promise<void>} */
  [Symbol.asyncDispose]() {
    return this.disconnect();
  }

  /**
   * One connect attempt (C# `TryConnectAsyncCore`). J10 calls it with `isReconnecting` for reconnects.
   *
   * @protected
   * @param {boolean} isReconnecting
   * @param {AbortSignal} [signal]
   * @returns {Promise<ConnectionResult>}
   */
  async _tryConnectCore(isReconnecting, signal) {
    return this.#tryConnectCore(isReconnecting, signal);
  }

  /**
   * @param {boolean} isReconnecting
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<ConnectionResult>}
   */
  async #tryConnectCore(isReconnecting, signal) {
    if (this.#session !== null) return SUCCESS;
    if (signal?.aborted) return canceled();
    const options = this.#options;

    this.#warnPlaintextOnce();
    this.#setState(isReconnecting ? ConnectionState.Reconnecting : ConnectionState.Connecting);

    // F05: the token is acquired before the transport connects, bounded by the handshake timeout. A failing provider
    // opens no connection.
    /** @type {Uint8Array | null} */
    let token;
    try {
      token = await this.#authenticate(signal);
    } catch (error) {
      this.#setState(ConnectionState.Disconnected);
      if (signal?.aborted) return canceled();
      this.#log('error', 'Could not acquire the authentication token.', error);
      return { success: false, state: ConnectionResultState.Exception, disconnectReason: DisconnectReason.Authentication, error };
    }

    const ctor = /** @type {typeof NexusClient} */ (this.constructor);
    const factory = /** @type {import('../transport/ITransport.js').TransportFactory} */ (ctor.transports[options.kind]);
    /** @type {import('../transport/ITransport.js').Transport} */
    let transport;
    const timeout = this.#timeoutSignal(options.connectionTimeout, 'Connection timed out.');
    try {
      transport = await factory(this.config, { signal: anySignal(signal, timeout.signal) });
    } catch (error) {
      this.#setState(ConnectionState.Disconnected);
      if (signal?.aborted) return canceled();
      if (error instanceof NexusTransportError) {
        return { success: false, state: ConnectionResultState.Disconnected, disconnectReason: error.reason || DisconnectReason.SocketError, error };
      }
      if (timeout.signal.aborted) {
        const timedOut = new NexusTransportError(`Connection timed out after ${options.connectionTimeout} ms.`, { reason: DisconnectReason.Timeout, cause: error });
        return { success: false, state: ConnectionResultState.Disconnected, disconnectReason: DisconnectReason.Timeout, error: timedOut };
      }
      return { success: false, state: ConnectionResultState.Exception, disconnectReason: DisconnectReason.SocketError, error };
    } finally {
      timeout.cancel();
    }

    const session = new NexusSession({ transport, options, handshake: this._binding.handshake, isReconnecting });
    this.#attachSessionLayers(session);
    this.#session = session;
    session.onStateChange = (state) => this.#setState(state);
    const disconnected = new Promise((resolve) => {
      session.onClose((reason) => {
        this.#onSessionClosed(session, reason);
        resolve(undefined);
      });
    });
    this.#disconnected = /** @type {Promise<void>} */ (disconnected);

    const onAbort = () => void session.abort(DisconnectReason.SocketError);
    signal?.addEventListener('abort', onAbort, { once: true });
    try {
      if (signal?.aborted) {
        await session.abort(DisconnectReason.SocketError);
      } else {
        session.start(token);
      }
      const ok = await session.ready;
      if (!ok) {
        await session.closed;
        if (signal?.aborted) return canceled();
        return { success: false, state: ConnectionResultState.Disconnected, disconnectReason: session.disconnectReason };
      }
    } finally {
      signal?.removeEventListener('abort', onAbort);
    }

    if (this.#proxy === null) this.#proxy = new this._binding.proxy(this.#handle);
    const nexus = this.nexus;
    queueMicrotask(() => this.#callHook('onConnected', () => nexus.onConnected(isReconnecting)));
    return SUCCESS;
  }

  /**
   * Attaches the per-session layers before the session starts, so frames that arrive in the same read as the
   * ServerGreeting find their handlers. The invocation layer (J07) fails its pending calls when the session closes; the
   * pipe layer (J08) resolves pipe parameters of dispatched invocations and ends its pipes with the session.
   * @param {NexusSession} session
   */
  #attachSessionLayers(session) {
    const invocations = attachInvocations(session, { nexus: this.nexus, handshake: this._binding.handshake });
    attachPipes(session, invocations);
  }

  /**
   * @param {NexusSession} session
   * @param {number} reason
   */
  #onSessionClosed(session, reason) {
    if (this.#session !== session) return;
    this.#session = null;
    // A session that never completed its handshake is reported through the connect result only.
    if (!session.isHandshakeComplete) return;
    const nexus = this.nexus;
    queueMicrotask(() => {
      this.dispatchEvent(new CustomEvent('disconnected', { detail: /** @type {DisconnectedDetail} */ ({ reason }) }));
      this.#callHook('onDisconnected', () => nexus.onDisconnected(reason));
    });
  }

  /**
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<Uint8Array | null>}
   */
  async #authenticate(signal) {
    const authenticate = this.#options.authenticate;
    if (authenticate === null) return null;
    signal?.throwIfAborted();
    const ms = this.#options.handshakeTimeout;
    const timeout = this.#timeoutSignal(ms, 'authenticate timed out.');
    try {
      const pending = authenticate({ signal: anySignal(signal, timeout.signal) });
      const token = await new Promise((resolve, reject) => {
        const onAbort = () => {
          if (signal?.aborted) reject(cancellationError(signal));
          else reject(new NexusTransportError(`authenticate did not complete within handshakeTimeout (${ms} ms).`, { reason: DisconnectReason.Authentication }));
        };
        if (signal?.aborted || timeout.signal.aborted) return onAbort();
        signal?.addEventListener('abort', onAbort, { once: true });
        timeout.signal.addEventListener('abort', onAbort, { once: true });
        Promise.resolve(pending).then(resolve, reject).finally(() => {
          signal?.removeEventListener('abort', onAbort);
          timeout.signal.removeEventListener('abort', onAbort);
        });
      });
      if (token === null || token === undefined) return null;
      if (!(token instanceof Uint8Array)) throw new TypeError('authenticate must return a Uint8Array, null or undefined');
      return token;
    } finally {
      timeout.cancel();
    }
  }

  /**
   * An AbortSignal that aborts with a `TimeoutError` after `ms` on the client clock.
   * @param {number} ms
   * @param {string} message
   * @returns {{ signal: AbortSignal, cancel: () => void }}
   */
  #timeoutSignal(ms, message) {
    const clock = this.#options.clock;
    const controller = new AbortController();
    const handle = clock.setTimeout(() => controller.abort(new DOMException(message, 'TimeoutError')), ms);
    return { signal: controller.signal, cancel: () => clock.clearTimeout(handle) };
  }

  #warnPlaintextOnce() {
    if (this.#options.isSecure || this.#plaintextWarned) return;
    this.#plaintextWarned = true;
    this.#log('warning', `NexNet: unencrypted transport (${this.#options.transportName}); credentials and data are visible on the network.`);
  }

  /** @param {import('../contract/enums.js').ConnectionStateValue} state */
  #setState(state) {
    const previous = this.#state;
    if (previous === state) return;
    this.#state = state;
    queueMicrotask(() => {
      this.dispatchEvent(new CustomEvent('statechange', { detail: /** @type {StateChangeDetail} */ ({ state, previous }) }));
    });
  }

  /**
   * Runs a nexus hook; a throw or rejection is logged, never propagated.
   * @param {string} name
   * @param {() => void | Promise<void>} hook
   */
  #callHook(name, hook) {
    try {
      const result = hook();
      if (result && typeof result.then === 'function') {
        result.then(undefined, (error) => this.#log('error', `${name} threw.`, error));
      }
    } catch (error) {
      this.#log('error', `${name} threw.`, error);
    }
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   */
  #log(level, message, error) {
    const logger = this.#options.logger;
    if (!logger) return;
    try {
      logger.log(level, message, error);
    } catch {
      // A logger must never break the client.
    }
  }
}

/** @returns {ConnectionResult} */
function canceled() {
  return { success: false, state: ConnectionResultState.Canceled, disconnectReason: DisconnectReason.None };
}

/**
 * @param {AbortSignal | undefined} a
 * @param {AbortSignal} b
 * @returns {AbortSignal}
 */
function anySignal(a, b) {
  return a ? AbortSignal.any([a, b]) : b;
}

/** @param {number} reason @returns {string} */
function describeReason(reason) {
  const names = /** @type {Record<string, number>} */ (DisconnectReason);
  const name = Object.keys(names).find((key) => names[key] === reason);
  return name ? `${name} (${reason})` : `reason ${reason}`;
}

/**
 * @param {import('../contract/handshake.js').ClientBinding<any>} binding
 */
function validateBinding(binding) {
  if (typeof binding !== 'object' || binding === null || typeof binding.handshake !== 'object' || binding.handshake === null) {
    throw new TypeError('binding.handshake is required (pass the generated binding)');
  }
  const { version, serverNexusHash, clientNexusHash } = binding.handshake;
  if (version !== null && (typeof version !== 'string' || version.length > MAX_VERSION_LENGTH)) {
    throw new TypeError(`binding.handshake.version must be null or a string of at most ${MAX_VERSION_LENGTH} characters`);
  }
  for (const [name, value] of [['serverNexusHash', serverNexusHash], ['clientNexusHash', clientNexusHash]]) {
    if (typeof value !== 'number' || !Number.isInteger(value) || value < -0x80000000 || value > 0x7fffffff) {
      throw new TypeError(`binding.handshake.${name} must be an int32`);
    }
  }
  if (typeof binding.proxy !== 'function') throw new TypeError('binding.proxy must be the generated proxy class');
}
