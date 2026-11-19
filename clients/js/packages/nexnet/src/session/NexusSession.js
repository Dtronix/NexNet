/**
 * One NexNet session over a connected transport: framing, handshake, keepalive, timeouts and disconnect (protocol
 * spec §1, §2, §5, §9). Invocations, pipes and collections (J07–J09) plug in through {@link NexusSession#setHandler},
 * {@link NexusSession#send}, {@link NexusSession#controlSender}, {@link NexusSession#onClose} and
 * {@link NexusSession#addProgressProbe}.
 *
 * Every complete frame is handled synchronously in the transport's data callback. The session never calls user code
 * synchronously from there: the client turns state changes into events in a microtask.
 * @module
 */

import { ConnectionState, DisconnectReason } from '../contract/enums.js';
import { ControlSender } from './ControlSender.js';
import { FrameReader } from './FrameReader.js';
import { countSession } from './diagnostics.js';
import { encodeFrame, encodeHeaderOnly, encodePreamble } from './frames.js';
import { startIntervalTimer, systemClock } from './clock.js';
import { effectivePingInterval } from './options.js';
import { notifySessionStarted } from './observers.js';
import { MIN_PEER_MAX_MESSAGE_SIZE, MessageType } from './protocol.js';
import { WriteQueue } from './WriteQueue.js';
import { decodeServerGreeting, encodeClientGreeting } from './wire.js';

/**
 * Longest a disconnect waits for its disconnect byte to be written before closing anyway (C#: 5 s write-lock wait).
 * The wait is also cut short by the progress timeout (§9): see `#disconnectWriteLimit`.
 */
const DISCONNECT_WRITE_TIMEOUT = 5000;

/**
 * Handles one received frame type after the connection completed. Returns a disconnect reason to end the session
 * (the disconnect byte is sent), or nothing to continue. Must not keep `frame.body` past the call without copying.
 *
 * @callback FrameHandler
 * @param {import('./FrameReader.js').Frame} frame
 * @returns {number | void}
 */

/**
 * Contributes to the session's snapshot (`getSnapshot()`): the pipe layer supplies `pipes`, the invocation layer
 * `largeMessages`. `now` is the session clock's time.
 *
 * @typedef {object} SnapshotSource
 * @property {(now: number) => import('../diagnostics/snapshot.js').NexusPipeSnapshot[]} [pipes]
 * @property {(now: number) => import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]} [largeMessages]
 */

/**
 * Reports a lack of progress (§9): returns a description of what stalled, or null.
 *
 * @callback ProgressProbe
 * @param {number} now Clock time.
 * @param {number} progressTimeout
 * @returns {string | null}
 */

export class NexusSession {
  /** @type {import('../transport/ITransport.js').Transport} */
  #transport;
  /** @type {import('./options.js').ResolvedClientOptions} */
  #options;
  /** @type {import('../contract/handshake.js').HandshakeDescriptor} */
  #handshake;
  #isReconnecting;
  #reader = new FrameReader();
  /** @type {WriteQueue} */
  #writer;
  /** @type {Map<number, FrameHandler>} */
  #handlers = new Map();
  /** @type {Array<(reason: number) => void>} */
  #closeListeners = [];
  /** @type {ProgressProbe[]} */
  #probes = [];
  /** @type {import('./options.js').ResolvedClientOptions['clock']} */
  #clock;
  /** @type {import('./observers.js').MetricsSink[]} */
  #metricsSinks = [];
  /** @type {SnapshotSource[]} */
  #snapshotSources = [];

  /** @type {import('../contract/enums.js').ConnectionStateValue} */
  #state;
  #started = false;
  #handshakeComplete = false;
  #disconnectStarted = false;
  /** @type {(() => void) | null} */
  #cancelHandshakeTimer = null;
  /** @type {import('./clock.js').IntervalTimer | null} */
  #keepalive = null;

  /** @type {(ok: boolean) => void} */
  #resolveReady = () => {};
  /** @type {(reason: number) => void} */
  #resolveClosed = () => {};

  /** Resolves true when the handshake completes, false when the session ends first. Never rejects. @type {Promise<boolean>} */
  ready;
  /** Resolves with the disconnect reason once the session is `Disconnected`. Never rejects. @type {Promise<number>} */
  closed;

  /** Session ID assigned by the server; null before the handshake completes. @type {bigint | null} */
  id = null;
  /** Why the session ended; `None` while it is open. @type {number} */
  disconnectReason = DisconnectReason.None;
  /** The peer's advertised `maxMessageSize`; 0 before the handshake. */
  peerMaxMessageSize = 0;
  /** The peer's advertised `invocationCredits`; 0 before the handshake. */
  peerInvocationCredits = 0;
  /** The server's advertised idle timeout (S04), or null when it does not send one. @type {number | null} */
  idleTimeoutMs = null;
  /** The ping interval in use; 0 before the handshake. */
  pingInterval = 0;
  /** Clock time the session was created, which is when its transport connected. */
  connectedAt;
  /** Clock time of the last complete frame received. */
  lastReceived;
  framesReceived = 0;
  bytesReceived = 0;

  /** Coalesced control replies (§5.5, §7.2, §6.3, §4.5, §8). @type {ControlSender} */
  controlSender;

  /**
   * Called synchronously on every state change. The client defers it to an event.
   * @type {((state: import('../contract/enums.js').ConnectionStateValue) => void) | null}
   */
  onStateChange = null;

  /**
   * @param {object} init
   * @param {import('../transport/ITransport.js').Transport} init.transport A connected transport.
   * @param {import('./options.js').ResolvedClientOptions} init.options
   * @param {import('../contract/handshake.js').HandshakeDescriptor} init.handshake
   * @param {boolean} [init.isReconnecting] Sends `ClientGreetingReconnection` (101) instead of `ClientGreeting` (100).
   */
  constructor({ transport, options, handshake, isReconnecting = false }) {
    this.#transport = transport;
    this.#options = options;
    this.#handshake = handshake;
    this.#isReconnecting = isReconnecting;
    this.#clock = options.clock;
    this.#state = isReconnecting ? ConnectionState.Reconnecting : ConnectionState.Connecting;
    this.connectedAt = this.#clock.now();
    this.lastReceived = this.connectedAt;
    this.ready = new Promise((resolve) => { this.#resolveReady = resolve; });
    this.closed = new Promise((resolve) => { this.#resolveClosed = resolve; });
    this.controlSender = new ControlSender(() => this.#writer.schedule(), options.invocationCredits / 2);
    this.#writer = new WriteQueue(transport, {
      beforeFlush: () => this.controlSender.drain(),
      hasControl: () => this.controlSender.hasPending,
      onError: (error) => {
        this.#log('debug', 'Transport write failed.', error);
        void this.#disconnectCore(DisconnectReason.SocketClosedWhenWriting, false);
      },
      now: () => this.#clock.now(),
    });
    countSession('openSessions', 1);
  }

  /** @returns {import('../contract/enums.js').ConnectionStateValue} */
  get state() {
    return this.#state;
  }

  /** True once the handshake completed. @returns {boolean} */
  get isHandshakeComplete() {
    return this.#handshakeComplete;
  }

  /** True once a disconnect started. @returns {boolean} */
  get isDisconnecting() {
    return this.#disconnectStarted;
  }

  /** The transport's own security flag. @returns {boolean} */
  get transportIsSecure() {
    return this.#transport.isSecure === true;
  }

  /** @returns {unknown} */
  get remoteCertificate() {
    return this.#transport.remoteCertificate ?? null;
  }

  /** The resolved client options. @returns {import('./options.js').ResolvedClientOptions} */
  get options() {
    return this.#options;
  }

  /** Largest value this side may send: the smaller of its own `maxMessageSize` and the peer's (§5). @returns {number} */
  get sendMaxMessageSize() {
    return Math.min(this.#options.maxMessageSize, this.peerMaxMessageSize);
  }

  /** Frames and bytes handed to the transport. @returns {number} */
  get framesSent() {
    return this.#writer.framesSent;
  }

  /** @returns {number} */
  get bytesSent() {
    return this.#writer.bytesSent;
  }

  /** Frames queued and not yet written. @returns {number} */
  get pendingWrites() {
    return this.#writer.pendingCount;
  }

  /**
   * Registers the handler of a message type received after the handshake. A type without a handler is a protocol
   * error. `Ping` and the greetings are handled by the session and can't be registered.
   * @param {number} type
   * @param {FrameHandler} handler
   * @returns {void}
   */
  setHandler(type, handler) {
    if (type === MessageType.Ping || type === MessageType.ServerGreeting
      || type === MessageType.ClientGreeting || type === MessageType.ClientGreetingReconnection) {
      throw new TypeError(`message type ${type} is handled by the session`);
    }
    this.#handlers.set(type, handler);
  }

  /**
   * Registers a listener called once, with the reason, when the session reaches `Disconnected`.
   * @param {(reason: number) => void} listener
   * @returns {void}
   */
  onClose(listener) {
    this.#closeListeners.push(listener);
  }

  /**
   * Adds a progress probe checked on every keepalive tick (§9).
   * @param {ProgressProbe} probe
   * @returns {void}
   */
  addProgressProbe(probe) {
    this.#probes.push(probe);
  }

  /**
   * Adds a snapshot source (the pipe and invocation layers register theirs when they attach).
   * @param {SnapshotSource} source
   * @returns {void}
   */
  addSnapshotSource(source) {
    this.#snapshotSources.push(source);
  }

  /**
   * The session-layer part of the snapshot (C# `NexusSessionSnapshot`): milliseconds since the transport connected,
   * since data was last written to the transport (null if nothing was), and how long the transport write in flight
   * has been running (null if none is).
   * @param {number} [now] Session clock time.
   * @returns {{ connected: number, sinceLastSent: number | null, flushInProgress: number | null }}
   */
  snapshot(now = this.#clock.now()) {
    const lastSent = this.#writer.lastSentAt;
    return {
      connected: Math.max(0, now - this.connectedAt),
      sinceLastSent: lastSent === null ? null : Math.max(0, now - lastSent),
      flushInProgress: this.#writer.inFlight ? Math.max(0, now - this.#writer.flushStartedAt) : null,
    };
  }

  /**
   * Snapshots of the pipes holding a receive window (from the pipe layer).
   * @param {number} [now]
   * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot[]}
   */
  pipeSnapshots(now = this.#clock.now()) {
    return this.#snapshotSources.flatMap((source) => source.pipes?.(now) ?? []);
  }

  /**
   * Snapshots of the arguments and results larger than one frame being received or sent (from the invocation layer).
   * @param {number} [now]
   * @returns {import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]}
   */
  largeMessageSnapshots(now = this.#clock.now()) {
    return this.#snapshotSources.flatMap((source) => source.largeMessages?.(now) ?? []);
  }

  /**
   * Attaches a metrics sink (the metrics layer does, for sessions of tracked clients). Returns a function that
   * detaches it. Sinks are dropped when the session reaches `Disconnected`.
   * @param {import('./observers.js').MetricsSink} sink
   * @returns {() => void}
   */
  addMetricsSink(sink) {
    this.#metricsSinks.push(sink);
    return () => {
      this.#metricsSinks = this.#metricsSinks.filter((s) => s !== sink);
    };
  }

  /**
   * Reports an event instrument measurement to the attached sinks (see {@link import('./observers.js').MetricsSink}).
   * Does nothing when none is attached.
   * @param {string} name A key of `Instruments`.
   * @param {number} value
   * @param {Readonly<Record<string, string>>} [tags]
   * @returns {void}
   */
  recordMetric(name, value, tags) {
    for (const sink of this.#metricsSinks) {
      try {
        sink(name, value, tags);
      } catch {
        // Metrics must never break the session.
      }
    }
  }

  /**
   * Queues an encoded frame. Resolves true once written, false when the session ended first. Frames queued in the
   * same tick go out in one transport write.
   * @param {Uint8Array} frame
   * @returns {Promise<boolean>}
   */
  send(frame) {
    if (this.#disconnectStarted || !this.#handshakeComplete) return Promise.resolve(false);
    return this.#writer.enqueue(frame);
  }

  /**
   * Starts the session: reads from the transport, sends the preamble and the greeting, and starts the handshake
   * timeout. Call once.
   * @param {Uint8Array | null} authenticationToken
   * @returns {void}
   */
  start(authenticationToken) {
    if (this.#started) throw new Error('session already started');
    this.#started = true;
    const greeting = encodeClientGreeting({
      version: this.#handshake.version,
      serverNexusHash: this.#handshake.serverNexusHash,
      clientNexusHash: this.#handshake.clientNexusHash,
      authenticationToken,
      maxMessageSize: this.#options.maxMessageSize,
      invocationCredits: this.#options.invocationCredits,
    });
    const type = this.#isReconnecting ? MessageType.ClientGreetingReconnection : MessageType.ClientGreeting;
    notifySessionStarted(this);

    // §5.1: the handshake timeout starts with the read loop. It ends the session without a disconnect byte.
    this.#cancelHandshakeTimer = this.#timer(this.#options.handshakeTimeout, () => {
      this.#cancelHandshakeTimer = null;
      if (this.#handshakeComplete) return;
      this.#log('information', `Handshake did not complete within ${this.#options.handshakeTimeout} ms.`);
      void this.#disconnectCore(DisconnectReason.Timeout, false);
    });

    this.#transport.onClose((error) => {
      if (this.#disconnectStarted) return;
      this.#log('debug', 'Transport closed.', error);
      void this.#disconnectCore(DisconnectReason.SocketError, false);
    });
    this.#transport.onData((chunk) => this.#onData(chunk));

    if (this.#disconnectStarted) return;
    void this.#writer.enqueue(encodePreamble());
    void this.#writer.enqueue(encodeFrame(type, greeting));
  }

  /**
   * Ends the session with `reason` and sends the disconnect byte (§5.6). Resolves once the session is `Disconnected`.
   * @param {number} [reason] Default `Graceful`.
   * @returns {Promise<void>}
   */
  disconnect(reason = DisconnectReason.Graceful) {
    return this.#disconnectCore(reason, true);
  }

  /**
   * Ends the session without sending a disconnect byte (an aborted connect, a transport that ended).
   * @param {number} reason
   * @returns {Promise<void>}
   */
  abort(reason) {
    return this.#disconnectCore(reason, false);
  }

  /**
   * Checks the idle timeout (§5.5) and the progress timeout (§9); disconnects and returns true when one expired.
   * Runs on every keepalive tick.
   * @returns {boolean}
   */
  checkTimeouts() {
    if (!this.#handshakeComplete || this.#disconnectStarted) return false;
    const now = this.#clock.now();
    if (now - this.lastReceived > this.#options.timeout) {
      this.#log('debug', `No frame received for ${this.#options.timeout} ms.`);
      void this.#disconnectCore(DisconnectReason.Timeout, true);
      return true;
    }
    const progress = this.#options.progressTimeout;
    let stall = this.#writer.inFlight && now - this.#writer.flushStartedAt > progress
      ? `outbound transport write running for ${Math.round(now - this.#writer.flushStartedAt)} ms`
      : null;
    for (const probe of this.#probes) {
      if (stall) break;
      stall = probe(now, progress);
    }
    if (!stall) return false;
    this.#log('warning', `Session made no progress within ${progress} ms: ${stall}.`);
    void this.#disconnectCore(DisconnectReason.Stalled, true);
    return true;
  }

  /** @param {Uint8Array} chunk */
  #onData(chunk) {
    if (this.#disconnectStarted) return;
    this.bytesReceived += chunk.length;
    const result = this.#reader.push(chunk, (frame) => this.#onFrame(frame));
    if (result.kind === 'disconnect') {
      this.#log('information', `Server disconnected with reason ${result.reason}.`);
      void this.#disconnectCore(result.reason, false);
    } else if (result.kind === 'error') {
      this.#log('warning', `Protocol error: ${result.message}.`);
      void this.#disconnectCore(result.reason, true);
    }
  }

  /**
   * @param {import('./FrameReader.js').Frame} frame
   * @returns {boolean}
   */
  #onFrame(frame) {
    if (this.#disconnectStarted) return false;
    this.framesReceived++;
    this.lastReceived = this.#clock.now();
    let reason;
    try {
      reason = this.#handleFrame(frame) || 0;
    } catch (error) {
      this.#log('error', `Handling message type ${frame.type} failed.`, error);
      reason = DisconnectReason.ProtocolError;
    }
    if (reason === 0) return true;
    void this.#disconnectCore(reason, true);
    return false;
  }

  /**
   * @param {import('./FrameReader.js').Frame} frame
   * @returns {number}
   */
  #handleFrame(frame) {
    const type = frame.type;
    if (!this.#handshakeComplete) {
      // §5.1: before the connection completes only ServerGreeting (or a disconnect byte) is accepted.
      if (type !== MessageType.ServerGreeting) {
        this.#log('warning', `Received message type ${type} before the connection completed.`);
        return DisconnectReason.ProtocolError;
      }
      return this.#onServerGreeting(frame.body);
    }
    switch (type) {
      case MessageType.Ping:
        return 0; // The server's reply; receiving any frame already refreshed the idle timeout.
      case MessageType.ServerGreeting:
        this.#log('warning', 'Server sent multiple server greetings.');
        return DisconnectReason.ProtocolError;
      case MessageType.ClientGreeting:
      case MessageType.ClientGreetingReconnection:
        this.#log('warning', 'Received a client greeting on the client.');
        return DisconnectReason.ProtocolError;
      default: {
        const handler = this.#handlers.get(type);
        if (!handler) {
          this.#log('warning', `No handler for message type ${type}.`);
          return DisconnectReason.ProtocolError;
        }
        return handler(frame) || 0;
      }
    }
  }

  /**
   * §5.2, client checks.
   * @param {Uint8Array} body
   * @returns {number}
   */
  #onServerGreeting(body) {
    /** @type {import('./wire.js').ServerGreeting} */
    let greeting;
    try {
      greeting = decodeServerGreeting(body);
    } catch (error) {
      this.#log('warning', 'ServerGreeting did not decode.', error);
      return DisconnectReason.ProtocolError;
    }
    if (greeting.version !== this.#handshake.clientNexusHash) {
      this.#log('warning', `Server expects client nexus hash ${greeting.version}; this client is ${this.#handshake.clientNexusHash}.`);
      return DisconnectReason.ClientMismatch;
    }
    if (greeting.maxMessageSize < MIN_PEER_MAX_MESSAGE_SIZE || greeting.invocationCredits < 1) {
      this.#log('warning', `Server advertised invalid limits (maxMessageSize ${greeting.maxMessageSize}, invocationCredits ${greeting.invocationCredits}).`);
      return DisconnectReason.ProtocolError;
    }

    this.id = greeting.clientId;
    this.peerMaxMessageSize = greeting.maxMessageSize;
    this.peerInvocationCredits = greeting.invocationCredits;
    this.idleTimeoutMs = greeting.idleTimeoutMs !== null && greeting.idleTimeoutMs > 0 ? greeting.idleTimeoutMs : null;
    this.pingInterval = effectivePingInterval(this.#options.pingInterval, this.idleTimeoutMs);
    if (this.#options.pingInterval !== null && this.idleTimeoutMs !== null && this.pingInterval >= this.idleTimeoutMs) {
      this.#log('warning', `PingInterval (${this.pingInterval} ms) is not below the server's idle timeout (${this.idleTimeoutMs} ms); `
        + 'the server may time the session out between pings.');
    }

    this.#handshakeComplete = true;
    this.#reader.handshakeComplete = true;
    this.#cancelHandshakeTimer?.();
    this.#cancelHandshakeTimer = null;

    // §5.5: only the client pings, starting once the connection completes; each tick first checks the timeouts.
    this.#keepalive = startIntervalTimer(this.#clock, this.pingInterval, () => this.#onKeepalive());
    countSession('timers', 1);

    this.#setState(ConnectionState.Connected);
    this.#resolveReady(true);
    return 0;
  }

  #onKeepalive() {
    if (this.checkTimeouts()) return;
    void this.#writer.enqueue(encodeHeaderOnly(MessageType.Ping));
  }

  /**
   * @param {number} reason
   * @param {boolean} sendByte
   * @returns {Promise<void>}
   */
  async #disconnectCore(reason, sendByte) {
    if (this.#disconnectStarted) {
      await this.closed;
      return;
    }
    this.#disconnectStarted = true;
    this.disconnectReason = reason;
    this.#setState(ConnectionState.Disconnecting);

    this.#cancelHandshakeTimer?.();
    this.#cancelHandshakeTimer = null;
    if (this.#keepalive) {
      this.#keepalive.stop();
      this.#keepalive = null;
      countSession('timers', -1);
    }
    this.controlSender.clear();

    if (sendByte && this.#started) {
      // The byte queues behind any write in flight and the frames already queued (§5.6). A transport write that never settles (a stuck socket) must not
      // hold the close: past the limit the session closes without the byte (J20).
      const limit = this.#disconnectWriteLimit();
      const finished = this.#writer.finish(encodeHeaderOnly(reason));
      const written = limit > 0 ? await this.#within(finished, limit) : false;
      const delayMs = this.#options.disconnectDelay;
      // Real time, never the injected clock (C# Task.Delay, Java SessionTimers.system(); J26): the delay gives the
      // network wall-clock time to carry the byte (§5.6), and a test clock that no longer moves must not hold the
      // session in Disconnecting.
      if (written && delayMs > 0) await new Promise((resolve) => { this.#timer(delayMs, () => resolve(undefined), systemClock); });
    }
    this.#writer.close();

    try {
      await this.#transport.close(reason);
    } catch (error) {
      this.#log('debug', 'Closing the transport failed.', error);
    }

    this.#setState(ConnectionState.Disconnected);
    countSession('openSessions', -1);
    this.#resolveReady(false);
    const listeners = this.#closeListeners;
    this.#closeListeners = [];
    for (const listener of listeners) {
      try {
        listener(reason);
      } catch (error) {
        this.#log('error', 'A session close listener threw.', error);
      }
    }
    this.#metricsSinks = [];
    this.#resolveClosed(reason);
  }

  /**
   * How long a disconnect starting now waits for its disconnect byte (§5.6): {@link DISCONNECT_WRITE_TIMEOUT}, cut to
   * what is left of `progressTimeout` (§9) for the transport write in flight, if any. A write running past
   * `progressTimeout` is a stall and will not carry the byte, so the wait is then 0: a graceful close, idle timeout or
   * stall over a transport whose write never resolves ends within `progressTimeout` (plus `disconnectDelay` once the
   * byte is written) of that write's start. The limit is measured on the session clock and waited in real time (J26).
   * @returns {number} Milliseconds; 0 when the byte can't be written in time.
   */
  #disconnectWriteLimit() {
    const elapsed = this.#writer.inFlight ? this.#clock.now() - this.#writer.flushStartedAt : 0;
    return Math.max(0, Math.min(DISCONNECT_WRITE_TIMEOUT, this.#options.progressTimeout - elapsed));
  }

  /**
   * Resolves `promise`'s value, or false after `ms` of real time: a stuck write is real work that an injected clock
   * does not drive (J26, as Java's close fallback).
   * @param {Promise<boolean>} promise
   * @param {number} ms
   * @returns {Promise<boolean>}
   */
  #within(promise, ms) {
    return new Promise((resolve) => {
      const cancel = this.#timer(ms, () => resolve(false), systemClock);
      promise.then((value) => {
        cancel();
        resolve(value);
      });
    });
  }

  /**
   * A one-shot timer on the session clock (or `clock`), counted in the diagnostics. Returns its cancel function.
   * @param {number} ms
   * @param {() => void} callback
   * @param {import('./clock.js').Clock} [clock]
   * @returns {() => void}
   */
  #timer(ms, callback, clock = this.#clock) {
    let active = true;
    countSession('timers', 1);
    const handle = clock.setTimeout(() => {
      if (!active) return;
      active = false;
      countSession('timers', -1);
      callback();
    }, ms);
    return () => {
      if (!active) return;
      active = false;
      countSession('timers', -1);
      clock.clearTimeout(handle);
    };
  }

  /** @param {import('../contract/enums.js').ConnectionStateValue} state */
  #setState(state) {
    if (this.#state === state) return;
    this.#state = state;
    this.onStateChange?.(state);
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
      logger.log(level, this.id === null ? message : `S${this.id}: ${message}`, error);
    } catch {
      // A logger must never break the session.
    }
  }
}
