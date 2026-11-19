/**
 * The invocation layer of one session (protocol spec §4.3–§4.10, §6, §8): outbound calls with invocation IDs,
 * credits, cancellation and results; server-to-client calls with method validation, two execution pools, per-call
 * cancellation and results; large arguments and results in both directions.
 *
 * The client attaches one manager to every session before the session starts ({@link attachInvocations}), so frames
 * that arrive in the same read as the ServerGreeting find their handlers. Frames are handled synchronously in the
 * transport's data callback; user code (nexus methods) and promise settlement never run re-entrantly from there.
 * @module
 */

import { DisconnectReason, RemoteExceptionDetail } from '../contract/enums.js';
import {
  NexusBusyError,
  NexusError,
  NexusMessageTooLargeError,
  NexusRemoteInvocationError,
  NexusTransportError,
  NexusUnauthorizedError,
  cancellationError,
  isCancellationError,
} from '../contract/errors.js';
import { notImplemented } from '../contract/internal.js';
import { encodeChunkFrame, encodeFrame } from '../session/frames.js';
import { MAX_FRAME_BODY, MessageType } from '../session/protocol.js';
import { WireFormatError, decodeInvocationCancellation, decodeInvocationCredit, decodeMessageAbort, encodeMessageAbort } from '../session/wire.js';
import { invocationCodec } from './codec.js';
import { CreditGate } from './CreditGate.js';
import { countInvocation } from './diagnostics.js';
import { LargeMessageReceiver } from './LargeMessageReceiver.js';
import {
  AbortReason,
  InvocationFlags,
  LargeMessageKind,
  ResultState,
  decodeInvocation,
  decodeInvocationResult,
  decodeMessageStart,
  encodeInvocation,
  encodeInvocationResult,
  encodeMessageStart,
  encodeRemoteErrorDetail,
  tryReadRemoteErrorDetail,
} from './messages.js';
import { SessionBudget } from './SessionBudget.js';

/** Largest serialized arguments that are never streamed (§6.1): 65,535 minus the 9-byte worst-case body overhead. */
export const MAX_ARGUMENT_SIZE = MAX_FRAME_BODY - 9;

/** Budget reserved for the inbound invocation queue per advertised credit (§9). */
const QUEUE_RESERVATION_PER_CREDIT = 64 * 1024;

/** Queue-wait warnings are rate limited to one per this many ms (C#: 30 s, shared by both pools). */
const QUEUE_WARNING_INTERVAL = 30_000;

/**
 * Optional hooks a nexus may implement (beyond the frozen contract): the C# `OnInvocationException` and the
 * generated `SeparatePool` table.
 *
 * @typedef {object} NexusHooks
 * @property {(methodId: number, reader: import('../contract/codec.js').MsgPackReader, ctx: import('../contract/nexus.js').DispatchContext) => void | Promise<void>} _dispatch
 * @property {(error: unknown, methodId: number) => ({ code?: number | null, message?: string | null } | null | undefined)} [onInvocationException]
 * @property {(methodId: number) => boolean} [_separatePool]
 */

/**
 * Pipe and channel resolution for dispatched invocations; installed by the pipe layer (J08).
 *
 * @typedef {object} PipeHooks
 * @property {(pipeId: number, ctx: import('../contract/nexus.js').DispatchContext) => import('../contract/pipes.js').NexusDuplexPipe} pipe
 * @property {(pipeId: number, formatter: import('../contract/codec.js').Formatter<any>, ctx: import('../contract/nexus.js').DispatchContext) => import('../contract/pipes.js').NexusDuplexChannel<any>} channel
 */

/**
 * @typedef {object} OutboundCall
 * @property {boolean} expectResult False for IgnoreReturn invocations.
 * @property {AbortSignal | undefined} signal
 * @property {'waiting' | 'sending' | 'sent'} phase
 * @property {number} id The reserved invocation ID, or -1.
 * @property {boolean} settled
 * @property {(value: Uint8Array | null) => void} resolve
 * @property {(error: unknown) => void} reject
 * @property {(() => void) | null} onAbort
 */

/**
 * @typedef {object} QueuedInvocation
 * @property {import('./messages.js').InvocationMessage} message
 * @property {AbortController} controller
 * @property {number} reserved Budget bytes held until the invocation finishes (a reassembled large argument).
 * @property {number} queuedAt
 */

/**
 * @typedef {object} InvocationPool
 * @property {boolean} separate
 * @property {number} max
 * @property {number} free
 * @property {QueuedInvocation[]} queue
 */

/** @type {WeakMap<object, InvocationManager>} */
const managers = new WeakMap();

/** @type {WeakMap<object, () => void>} */
const slotReleasers = new WeakMap();

/**
 * Attaches an invocation manager to a session that has not started yet.
 * @param {import('../session/NexusSession.js').NexusSession} session
 * @param {object} init
 * @param {NexusHooks | object} init.nexus The client nexus.
 * @param {import('../contract/handshake.js').HandshakeDescriptor} init.handshake
 * @returns {InvocationManager}
 */
export function attachInvocations(session, { nexus, handshake }) {
  const manager = new InvocationManager(session, /** @type {NexusHooks} */ (nexus), handshake);
  managers.set(session, manager);
  return manager;
}

/**
 * The manager of a session, or null.
 * @param {import('../session/NexusSession.js').NexusSession | null | undefined} session
 * @returns {InvocationManager | null}
 */
export function getInvocationManager(session) {
  return session ? managers.get(session) ?? null : null;
}

/**
 * Frees the execution slot of a dispatched invocation before it completes (a pipe or channel method whose pipe is
 * open, §8). Idempotent.
 * @param {import('../contract/nexus.js').DispatchContext} ctx
 * @returns {void}
 */
export function releaseInvocationSlot(ctx) {
  slotReleasers.get(ctx)?.();
}

/**
 * Builds the method-ID check for `acceptedIds`: a `Set` of method IDs, or a `BigUint64Array` of method IDs or of C#
 * keys `(clientNexusHash << 16) | methodId` (§4.3).
 * @param {import('../contract/handshake.js').HandshakeDescriptor} handshake
 * @returns {(methodId: number) => boolean}
 */
function acceptedIdMatcher(handshake) {
  const ids = /** @type {unknown} */ (handshake.acceptedIds);
  if (ids instanceof Set) return (methodId) => ids.has(methodId);
  if (ids instanceof BigUint64Array) {
    const hashKey = BigInt.asUintN(64, BigInt(handshake.clientNexusHash) << 16n);
    /** @type {Set<number>} */
    const set = new Set();
    for (const value of ids) {
      if (value <= 0xffffn) set.add(Number(value));
      else if ((value & ~0xffffn) === hashKey) set.add(Number(value & 0xffffn));
    }
    return (methodId) => set.has(methodId);
  }
  return () => false;
}

export class InvocationManager {
  /** @type {import('../session/NexusSession.js').NexusSession} */
  #session;
  /** @type {import('../session/options.js').ResolvedClientOptions} */
  #options;
  /** @type {NexusHooks} */
  #nexus;
  #accepts;
  #gate = new CreditGate();
  #creditsStarted = false;
  #closed = false;

  // Outbound.
  #lastId = 0;
  /** Reserved invocation IDs and the call holding each (§4.5: kept until the result arrives). @type {Map<number, OutboundCall>} */
  #idOwners = new Map();
  /** @type {Set<OutboundCall>} */
  #calls = new Set();
  /** Large messages this side is sending, by (ID, kind) key. @type {Map<number, { aborted: boolean, reason: number }>} */
  #outgoing = new Map();

  // Inbound.
  #inboundCredits;
  /** Controllers of received invocations that expect a result, by invocation ID. @type {Map<number, AbortController>} */
  #tracked = new Map();
  /** Shared by IgnoreReturn invocations, which only the session ending cancels. @type {AbortController | null} */
  #sharedController = null;
  /** @type {InvocationPool} */
  #normalPool;
  /** @type {InvocationPool} */
  #separatePool;
  #lastQueueWarning = -Infinity;
  #queueWarningsSuppressed = 0;

  /** The session's receive budget; the pipe layer (J08) reserves its windows from it. @type {SessionBudget} */
  budget;
  /** @type {LargeMessageReceiver} */
  #receiver;
  /** Installed by the pipe layer (J08). @type {PipeHooks | null} */
  pipeHooks = null;

  /**
   * @param {import('../session/NexusSession.js').NexusSession} session
   * @param {NexusHooks} nexus
   * @param {import('../contract/handshake.js').HandshakeDescriptor} handshake
   */
  constructor(session, nexus, handshake) {
    this.#session = session;
    this.#options = session.options;
    this.#nexus = nexus;
    this.#accepts = acceptedIdMatcher(handshake);
    this.#inboundCredits = this.#options.invocationCredits;
    this.#normalPool = { separate: false, max: this.#options.maxConcurrentConnectionInvocations, free: this.#options.maxConcurrentConnectionInvocations, queue: [] };
    this.#separatePool = { separate: true, max: this.#options.maxSeparatePoolInvocations, free: this.#options.maxSeparatePoolInvocations, queue: [] };

    this.budget = new SessionBudget(this.#options.sessionBudget);
    this.budget.tryReserve(this.#options.invocationCredits * QUEUE_RESERVATION_PER_CREDIT);
    this.#receiver = new LargeMessageReceiver({
      isCallOutstanding: (id) => this.#idOwners.get(id)?.expectResult === true,
      tryTakeInvocationCredit: () => --this.#inboundCredits >= 0,
      returnInvocationCredit: () => this.#returnInvocationCredit(),
      sendAbort: (id, kind, reason) => session.controlSender.requestAbort(id, kind, reason),
    }, this.budget, this.#options.maxMessageSize, () => this.#options.clock.now());

    session.setHandler(MessageType.Invocation, (frame) => this.#guard('Invocation', () => this.#handleInvocation(decodeInvocation(frame.body), false, 0)));
    session.setHandler(MessageType.InvocationResult, (frame) => this.#guard('InvocationResult', () => this.#handleResult(decodeInvocationResult(frame.body))));
    session.setHandler(MessageType.InvocationCancellation, (frame) => this.#guard('InvocationCancellation', () => this.#onCancellation(frame.body)));
    session.setHandler(MessageType.InvocationCredit, (frame) => this.#guard('InvocationCredit', () => this.#onCredit(frame.body)));
    session.setHandler(MessageType.MessageStart, (frame) => this.#guard('MessageStart', () => this.#receiver.onStart(decodeMessageStart(frame.body))));
    session.setHandler(MessageType.MessageChunk, (frame) => this.#guard('MessageChunk', () => this.#onChunk(frame)));
    session.setHandler(MessageType.MessageAbort, (frame) => this.#guard('MessageAbort', () => this.#onAbort(frame.body)));
    session.onClose((reason) => this.#onClose(reason));
    session.addProgressProbe((now, timeout) => this.#receiver.findStalled(now, timeout));
    void session.ready.then((ok) => {
      if (ok) this.#ensureCredits();
    });
  }

  /** True while calls can be sent. @returns {boolean} */
  get isConnected() {
    return !this.#closed && this.#session.isHandshakeComplete && !this.#session.isDisconnecting;
  }

  /** Reader options for results and arguments. @returns {import('../contract/codec.js').SerializerOptions} */
  get serializerOptions() {
    return this.#options.serializerOptions;
  }

  /** Outbound calls not yet completed. @returns {number} */
  get pendingCount() {
    return this.#calls.size;
  }

  /** Outbound invocation credits available now. @returns {number} */
  get availableCredits() {
    return this.#gate.available;
  }

  /** Server-to-client invocations queued or running. @returns {number} */
  get inboundCount() {
    return this.#normalPool.queue.length + this.#separatePool.queue.length
      + (this.#normalPool.max - this.#normalPool.free) + (this.#separatePool.max - this.#separatePool.free);
  }

  /**
   * Sends an invocation. With `expectResult` it resolves with the result value (the embedded MessagePack, or null when
   * the result carries none); without, it is an IgnoreReturn invocation that resolves once sent.
   *
   * Waits in FIFO order for invocation credit. A cancel before send rejects with the cancellation error and sends
   * nothing; a cancel after send (result calls only) rejects at once, sends `InvocationCancellation` and consumes the
   * late result (§4.5).
   *
   * @param {number} methodId
   * @param {Uint8Array | null} args The serialized arguments array; null for none.
   * @param {{ signal?: AbortSignal, flags?: number, expectResult: boolean }} options
   * @returns {Promise<Uint8Array | null>}
   */
  invoke(methodId, args, { signal, flags = 0, expectResult }) {
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    if (!this.isConnected) return Promise.reject(this.#notConnected());
    const sendMax = this.#session.sendMaxMessageSize;
    if ((args?.length ?? 0) > sendMax) return Promise.reject(tooLarge(args?.length ?? 0, sendMax));
    this.#ensureCredits();

    return new Promise((resolve, reject) => {
      /** @type {OutboundCall} */
      const call = { expectResult, signal, phase: 'waiting', id: -1, settled: false, resolve, reject, onAbort: null };
      this.#calls.add(call);
      countInvocation('pendingInvocations', 1);
      if (expectResult && signal) {
        call.onAbort = () => this.#onCallAborted(call);
        signal.addEventListener('abort', call.onAbort, { once: true });
      }
      if (this.#gate.tryTake()) {
        this.#sendCall(call, methodId, args, flags);
        return;
      }
      this.#gate.wait(signal).then(
        (acquired) => {
          if (acquired) this.#sendCall(call, methodId, args, flags);
          else this.#settle(call, false, this.#sessionEnded());
        },
        (error) => this.#settle(call, false, error),
      );
    });
  }

  /**
   * @param {OutboundCall} call
   * @param {number} methodId
   * @param {Uint8Array | null} args
   * @param {number} flags
   */
  #sendCall(call, methodId, args, flags) {
    if (call.settled || this.#closed) {
      if (!this.#closed) this.#gate.release(1); // Taken for nothing: nothing was sent.
      this.#settle(call, false, this.#sessionEnded());
      return;
    }
    const argLength = args?.length ?? 0;
    // A result call needs a unique ID; so does a streamed IgnoreReturn call, whose ID keys its stream (§4.3, §6.1).
    try {
      if (call.expectResult || argLength > MAX_ARGUMENT_SIZE) call.id = this.#reserveId(call);
    } catch (error) {
      this.#gate.release(1);
      this.#settle(call, false, error);
      return;
    }
    const wireFlags = call.expectResult ? flags : flags | InvocationFlags.IgnoreReturn;
    const body = encodeInvocation(Math.max(0, call.id), methodId, wireFlags, args);
    const sendMax = this.#session.sendMaxMessageSize;
    if (body.length > sendMax) {
      this.#releaseCallId(call);
      this.#gate.release(1);
      this.#settle(call, false, tooLarge(body.length, sendMax));
      return;
    }

    if (body.length <= MAX_FRAME_BODY) {
      void this.#session.send(encodeFrame(MessageType.Invocation, body));
      this.#onSent(call);
      return;
    }

    call.phase = 'sending';
    void this.#streamLarge(call.id, LargeMessageKind.Argument, MessageType.Invocation, body, call.expectResult ? call.signal : undefined)
      .then((outcome) => {
        if (outcome.result === 'sent') {
          this.#onSent(call);
          return;
        }
        this.#releaseCallId(call);
        if (outcome.result === 'canceled') this.#settle(call, false, cancellationError(call.signal));
        else if (outcome.result === 'refused') this.#settle(call, false, refusedError(outcome.reason));
        else this.#settle(call, false, this.#sessionEnded());
      }, (error) => {
        this.#releaseCallId(call);
        this.#settle(call, false, error);
      });
  }

  /** @param {OutboundCall} call */
  #onSent(call) {
    call.phase = 'sent';
    if (!call.expectResult) {
      this.#releaseCallId(call);
      this.#settle(call, true, null);
      return;
    }
    // Canceled while a large argument finished streaming: the peer has it now, so tell it.
    if (call.signal?.aborted) this.#cancelSent(call);
  }

  /** @param {OutboundCall} call */
  #onCallAborted(call) {
    // Waiting: the credit wait rejects. Sending: the stream stops and sends MessageAbort(Cancelled).
    if (call.settled || call.phase !== 'sent') return;
    this.#cancelSent(call);
  }

  /**
   * Cancel after send: completes the call at once and asks the peer to cancel. The ID stays reserved until the late
   * result arrives, which is then discarded (§4.5).
   * @param {OutboundCall} call
   */
  #cancelSent(call) {
    if (call.settled) return;
    this.#session.controlSender.requestInvocationCancellation(call.id);
    this.#settle(call, false, cancellationError(call.signal));
  }

  /**
   * @param {OutboundCall} call
   * @param {boolean} ok
   * @param {unknown} value
   */
  #settle(call, ok, value) {
    if (call.settled) return;
    call.settled = true;
    if (call.onAbort) call.signal?.removeEventListener('abort', call.onAbort);
    call.onAbort = null;
    this.#calls.delete(call);
    countInvocation('pendingInvocations', -1);
    if (ok) call.resolve(/** @type {Uint8Array | null} */ (value));
    else call.reject(value);
  }

  /**
   * @param {OutboundCall} call
   * @returns {number}
   */
  #reserveId(call) {
    for (let i = 0; i <= 0xffff; i++) {
      this.#lastId = (this.#lastId + 1) & 0xffff;
      if (!this.#idOwners.has(this.#lastId)) {
        this.#idOwners.set(this.#lastId, call);
        countInvocation('reservedInvocationIds', 1);
        return this.#lastId;
      }
    }
    throw new NexusError('All invocation IDs are in use.');
  }

  /** @param {OutboundCall} call */
  #releaseCallId(call) {
    if (call.id >= 0 && this.#idOwners.get(call.id) === call) this.#releaseId(call.id);
  }

  /** @param {number} id */
  #releaseId(id) {
    if (this.#idOwners.delete(id)) countInvocation('reservedInvocationIds', -1);
  }

  /**
   * Streams a body larger than one frame as MessageStart + MessageChunk frames (§6.1). Each chunk is queued after the
   * previous one was written, so other frames interleave. Stops on the receiver's refusal (answering Ack), on
   * `signal` (sending Cancelled) and when the session ends.
   *
   * @param {number} id
   * @param {number} kind
   * @param {number} innerType
   * @param {Uint8Array} body
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<{ result: 'sent' | 'refused' | 'canceled' | 'closed', reason: number }>}
   */
  async #streamLarge(id, kind, innerType, body, signal) {
    const key = id | (kind << 16);
    const entry = { aborted: false, reason: 0 };
    if (this.#outgoing.has(key)) throw new NexusError(`A large message for invocation ${id} is already being sent.`);
    this.#outgoing.set(key, entry);
    countInvocation('largeMessages', 1);
    const session = this.#session;
    try {
      const start = encodeMessageStart({ invocationId: id, kind, innerType, totalLength: body.length });
      if (!await session.send(encodeFrame(MessageType.MessageStart, start))) return { result: 'closed', reason: 0 };
      for (let offset = 0; offset < body.length;) {
        if (this.#closed) return { result: 'closed', reason: 0 };
        if (entry.aborted) {
          void session.send(encodeFrame(MessageType.MessageAbort, encodeMessageAbort(id, kind, AbortReason.Ack)));
          return { result: 'refused', reason: entry.reason };
        }
        if (signal?.aborted) {
          void session.send(encodeFrame(MessageType.MessageAbort, encodeMessageAbort(id, kind, AbortReason.Cancelled)));
          return { result: 'canceled', reason: 0 };
        }
        const count = Math.min(body.length - offset, MAX_FRAME_BODY);
        if (!await session.send(encodeChunkFrame(id, kind, body.subarray(offset, offset + count)))) return { result: 'closed', reason: 0 };
        offset += count;
      }
      return { result: 'sent', reason: 0 };
    } finally {
      if (this.#outgoing.get(key) === entry) {
        this.#outgoing.delete(key);
        countInvocation('largeMessages', -1);
      }
    }
  }

  // ---------------------------------------------------------------- received frames

  /**
   * Runs a frame handler; a body that does not decode is a protocol error.
   * @param {string} name
   * @param {() => number} handler
   * @returns {number}
   */
  #guard(name, handler) {
    this.#ensureCredits();
    try {
      return handler();
    } catch (error) {
      if (!(error instanceof WireFormatError)) throw error;
      this.#log('warning', `${name} did not decode: ${error.message}.`);
      return DisconnectReason.ProtocolError;
    }
  }

  /**
   * @param {import('./messages.js').InvocationResultMessage} result
   * @returns {number}
   */
  #handleResult(result) {
    const owner = this.#idOwners.get(result.invocationId);
    // A result for a call this side is not waiting on is a protocol error (§4.4).
    if (!owner || !owner.expectResult) {
      this.#log('warning', `Received a result for invocation ${result.invocationId}, which is not outstanding.`);
      return DisconnectReason.ProtocolError;
    }
    this.#releaseId(result.invocationId);
    // The late result of a call canceled after send is consumed here (§4.5).
    if (owner.settled) return 0;

    switch (result.state) {
      case ResultState.CompletedResult:
        this.#settle(owner, true, result.value);
        break;
      case ResultState.Exception: {
        const detail = tryReadRemoteErrorDetail(result.value);
        this.#settle(owner, false, remoteInvocationError(detail));
        break;
      }
      case ResultState.Unauthorized:
        this.#settle(owner, false, new NexusUnauthorizedError('The server refused the invocation: unauthorized.'));
        break;
      case ResultState.Canceled:
        this.#settle(owner, false, cancellationError(owner.signal?.aborted ? owner.signal : undefined));
        break;
      default:
        this.#settle(owner, false, new NexusError(`Invalid invocation result state ${result.state}.`));
        break;
    }
    return 0;
  }

  /** @param {Uint8Array} body @returns {number} */
  #onCredit(body) {
    const { credits } = decodeInvocationCredit(body);
    // Zero credit, or more than the peer advertised, breaks flow control (§4.7, §8).
    if (credits === 0 || this.#gate.available + credits > this.#session.peerInvocationCredits) {
      this.#log('warning', `Received invalid invocation credit (${credits}).`);
      return DisconnectReason.FlowControl;
    }
    this.#gate.release(credits);
    return 0;
  }

  /** @param {Uint8Array} body @returns {number} */
  #onCancellation(body) {
    const { invocationId } = decodeInvocationCancellation(body);
    // Not tracked: completed already, or never received. A queued one starts with its signal aborted.
    this.#tracked.get(invocationId)?.abort();
    return 0;
  }

  /** @param {import('../session/FrameReader.js').Frame} frame @returns {number} */
  #onChunk(frame) {
    if (frame.kind !== LargeMessageKind.Argument && frame.kind !== LargeMessageKind.Result) return DisconnectReason.ProtocolError;
    const { reason, completed } = this.#receiver.onChunk(frame.invocationId, frame.kind, frame.body);
    if (reason !== 0 || completed === null) return reason;
    // The last chunk: handle the reassembled message as if it had arrived in one frame. Its credit was taken at Start.
    if (completed.innerType === MessageType.Invocation) {
      let message;
      try {
        message = decodeInvocation(completed.body);
      } catch (error) {
        this.budget.release(completed.reserved);
        throw error;
      }
      const result = this.#handleInvocation(message, true, completed.reserved);
      if (result !== 0) this.budget.release(completed.reserved);
      return result;
    }
    try {
      return this.#handleResult(decodeInvocationResult(completed.body));
    } finally {
      this.budget.release(completed.reserved);
    }
  }

  /** @param {Uint8Array} body @returns {number} */
  #onAbort(body) {
    const abort = decodeMessageAbort(body);
    if (abort.reason === AbortReason.Busy || abort.reason === AbortReason.TooLarge) {
      // From the receiver of a stream this side is sending: stop it (the stream answers with Ack).
      const entry = this.#outgoing.get(abort.invocationId | (abort.kind << 16));
      if (entry && !entry.aborted) {
        entry.aborted = true;
        entry.reason = abort.reason;
      }
      // A refused argument fails its call, which will not run.
      if (abort.kind === LargeMessageKind.Argument) {
        const owner = this.#idOwners.get(abort.invocationId);
        if (owner?.expectResult) {
          this.#releaseId(abort.invocationId);
          this.#settle(owner, false, refusedError(abort.reason));
        }
      }
    } else {
      // From the sender of a stream this side is receiving.
      this.#receiver.onAbort(abort);
    }
    return 0;
  }

  /**
   * Validates and queues a server-to-client invocation (§4.3, §8).
   * @param {import('./messages.js').InvocationMessage} message
   * @param {boolean} creditTaken True for a reassembled large argument (its credit was taken at Start).
   * @param {number} reserved
   * @returns {number}
   */
  #handleInvocation(message, creditTaken, reserved) {
    if (!this.#accepts(message.methodId)) {
      this.#log('warning', `Server attempted to invoke method ${message.methodId}, which this client nexus does not accept.`);
      return DisconnectReason.ProtocolError;
    }
    if (!creditTaken && --this.#inboundCredits < 0) {
      this.#log('warning', 'Received an invocation without invocation credit.');
      return DisconnectReason.FlowControl;
    }

    let controller;
    if ((message.flags & InvocationFlags.IgnoreReturn) !== 0) {
      controller = this.#sharedController ??= new AbortController();
    } else {
      if (this.#tracked.has(message.invocationId)) {
        this.#log('warning', `Received invocation ${message.invocationId} while one with the same ID is outstanding.`);
        return DisconnectReason.ProtocolError;
      }
      controller = new AbortController();
      this.#tracked.set(message.invocationId, controller);
    }

    const pool = this.#nexus._separatePool?.(message.methodId) === true ? this.#separatePool : this.#normalPool;
    pool.queue.push({ message, controller, reserved, queuedAt: this.#options.clock.now() });
    countInvocation('inboundInvocations', 1);
    this.#dispatch(pool);
    return 0;
  }

  /**
   * Starts queued invocations while the pool has free slots. Invocation credit is returned at dequeue (§8).
   * @param {InvocationPool} pool
   */
  #dispatch(pool) {
    while (!this.#closed && pool.queue.length > 0 && pool.free > 0) {
      const item = /** @type {QueuedInvocation} */ (pool.queue.shift());
      pool.free--;
      this.#returnInvocationCredit();
      this.#checkQueueWait(item, pool);
      // Never runs user code from the data callback.
      queueMicrotask(() => void this.#execute(pool, item));
    }
  }

  #returnInvocationCredit() {
    this.#inboundCredits++;
    this.#session.controlSender.returnInvocationCredit();
  }

  /**
   * @param {QueuedInvocation} item
   * @param {InvocationPool} pool
   */
  #checkQueueWait(item, pool) {
    const threshold = this.#options.invocationQueueWarningThreshold;
    if (threshold === 0) return;
    const now = this.#options.clock.now();
    const waited = now - item.queuedAt;
    if (waited <= threshold) return;
    if (now - this.#lastQueueWarning < QUEUE_WARNING_INTERVAL) {
      this.#queueWarningsSuppressed++;
      return;
    }
    this.#lastQueueWarning = now;
    const suppressed = this.#queueWarningsSuppressed;
    this.#queueWarningsSuppressed = 0;
    const limit = pool.separate ? `maxSeparatePoolInvocations ${pool.max}` : `maxConcurrentConnectionInvocations ${pool.max}`;
    this.#log('warning', `Invocation of method ${item.message.methodId} waited ${Math.round(waited)} ms for an execution slot `
      + `(${limit}, threshold ${threshold} ms)${suppressed > 0 ? ` (${suppressed} more since the last warning)` : ''}.`);
  }

  /**
   * Runs one server-to-client invocation and sends its result.
   * @param {InvocationPool} pool
   * @param {QueuedInvocation} item
   */
  async #execute(pool, item) {
    const { message, controller } = item;
    const expectResult = (message.flags & InvocationFlags.IgnoreReturn) === 0;
    let slotHeld = true;
    const releaseSlot = () => {
      if (!slotHeld) return;
      slotHeld = false;
      pool.free++;
      this.#dispatch(pool);
    };
    /** @type {Uint8Array | null} */
    let result = null;
    const manager = this;
    /** @type {import('../contract/nexus.js').DispatchContext} */
    const ctx = {
      invocationId: message.invocationId,
      signal: controller.signal,
      writeResult(formatter, value) {
        const writer = invocationCodec().createWriter();
        formatter.write(writer, value);
        result = writer.toUint8Array().slice();
      },
      pipe(pipeId) {
        if (!manager.pipeHooks) throw notImplemented('DispatchContext.pipe');
        return manager.pipeHooks.pipe(pipeId, ctx);
      },
      channel(pipeId, formatter) {
        if (!manager.pipeHooks) throw notImplemented('DispatchContext.channel');
        return manager.pipeHooks.channel(pipeId, formatter, ctx);
      },
    };
    slotReleasers.set(ctx, releaseSlot);

    try {
      if (this.#closed) return;
      const reader = invocationCodec().createReader(message.args, this.#options.serializerOptions);
      await this.#nexus._dispatch(message.methodId, reader, ctx);
      if (!expectResult || this.#closed) return;
      const body = encodeInvocationResult(message.invocationId, ResultState.CompletedResult, result);
      const sendMax = this.#session.sendMaxMessageSize;
      if (body.length > sendMax) throw tooLarge(body.length, sendMax);
      await this.#sendResult(message.invocationId, body);
    } catch (error) {
      await this.#onExecuteError(message, controller, error, expectResult);
    } finally {
      if (expectResult && this.#tracked.get(message.invocationId) === controller) this.#tracked.delete(message.invocationId);
      releaseSlot();
      if (item.reserved > 0 && !this.#closed) this.budget.release(item.reserved);
      countInvocation('inboundInvocations', -1);
    }
  }

  /**
   * @param {import('./messages.js').InvocationMessage} message
   * @param {AbortController} controller
   * @param {unknown} error
   * @param {boolean} expectResult
   */
  async #onExecuteError(message, controller, error, expectResult) {
    const limit = error instanceof NexusMessageTooLargeError || error instanceof NexusBusyError;
    const canceled = !limit && (isCancellationError(error) || (controller.signal.aborted && error === controller.signal.reason));
    if (this.#closed) return;
    if (canceled) {
      this.#log('trace', `Invocation of method ${message.methodId} canceled.`);
    } else if (limit) {
      // A limit was reached; not a fault in the method.
      this.#log('warning', `Method ${message.methodId} failed: ${/** @type {Error} */ (error).message}`);
    } else {
      this.#log('error', `Invoked method ${message.methodId} threw.`, error);
    }
    if (!expectResult) return;
    const state = canceled ? ResultState.Canceled : ResultState.Exception;
    const detail = canceled ? null : this.#remoteErrorDetail(error, message.methodId);
    await this.#sendResult(message.invocationId, encodeInvocationResult(message.invocationId, state, detail));
  }

  /**
   * The detail sent with a failed invocation (§4.4): the nexus's `onInvocationException` error when it returns one,
   * else the type and message when `remoteExceptionDetail` is `TypeAndMessage`, else none.
   * @param {unknown} error
   * @param {number} methodId
   * @returns {Uint8Array | null}
   */
  #remoteErrorDetail(error, methodId) {
    /** @type {{ code?: number | null, message?: string | null } | null | undefined} */
    let custom = null;
    try {
      custom = this.#nexus.onInvocationException?.(error, methodId);
    } catch (hookError) {
      this.#log('error', 'onInvocationException threw.', hookError);
    }
    if (custom) {
      const code = typeof custom.code === 'number' && Number.isInteger(custom.code) && custom.code >= -0x80000000 && custom.code <= 0x7fffffff
        ? custom.code : null;
      return encodeRemoteErrorDetail({ code, type: null, message: custom.message ?? null });
    }
    if (this.#options.remoteExceptionDetail !== RemoteExceptionDetail.TypeAndMessage) return null;
    const type = error instanceof Error ? (error.constructor?.name || error.name) : typeof error;
    const text = error instanceof Error ? error.message : String(error);
    return encodeRemoteErrorDetail({ code: null, type, message: text });
  }

  /**
   * Sends an InvocationResult body, streamed when it exceeds one frame.
   * @param {number} invocationId
   * @param {Uint8Array} body
   * @returns {Promise<void>}
   */
  async #sendResult(invocationId, body) {
    if (body.length <= MAX_FRAME_BODY) {
      void this.#session.send(encodeFrame(MessageType.InvocationResult, body));
      return;
    }
    const outcome = await this.#streamLarge(invocationId, LargeMessageKind.Result, MessageType.InvocationResult, body, undefined);
    if (outcome.result === 'refused') this.#log('warning', `The server refused the result of invocation ${invocationId} (reason ${outcome.reason}).`);
  }

  // ---------------------------------------------------------------- lifecycle

  /** Takes the peer's advertised credits once the handshake completed. */
  #ensureCredits() {
    if (this.#creditsStarted || !this.#session.isHandshakeComplete) return;
    this.#creditsStarted = true;
    this.#gate.release(this.#session.peerInvocationCredits);
  }

  /**
   * The session ended: every pending call fails, queued invocations are dropped, running ones are canceled.
   * @param {number} reason
   */
  #onClose(reason) {
    this.#closed = true;
    this.#gate.close();
    this.budget.close();
    this.#receiver.clear();
    // A stream may wait on a transport write that never settles once the transport is gone: forget it here.
    for (const entry of this.#outgoing.values()) entry.aborted = true;
    countInvocation('largeMessages', -this.#outgoing.size);
    this.#outgoing.clear();

    const ended = this.#sessionEnded(reason);
    for (const call of [...this.#calls]) this.#settle(call, false, ended);
    countInvocation('reservedInvocationIds', -this.#idOwners.size);
    this.#idOwners.clear();

    for (const pool of [this.#normalPool, this.#separatePool]) {
      countInvocation('inboundInvocations', -pool.queue.length);
      for (const item of pool.queue) item.controller.abort();
      pool.queue = [];
    }
    for (const controller of this.#tracked.values()) controller.abort();
    this.#tracked.clear();
    this.#sharedController?.abort();
    this.#sharedController = null;
  }

  /** @returns {NexusTransportError} */
  #notConnected() {
    return new NexusTransportError('Session is not connected.', { reason: this.#session.disconnectReason });
  }

  /**
   * The rejection of a call still pending when the session ends: canceled, as in C# and spec §4.5 (amendment C-2),
   * with the transport error carrying the disconnect reason as its `cause`.
   * @param {number} [reason]
   * @returns {DOMException}
   */
  #sessionEnded(reason = this.#session.disconnectReason) {
    return sessionEndedError(reason);
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
      const id = this.#session.id;
      logger.log(level, id === null ? message : `S${id}: ${message}`, error);
    } catch {
      // A logger must never break the session.
    }
  }
}

/**
 * An `AbortError` whose `cause` is a `NexusTransportError` with the disconnect reason (amendment C-2). `cause` is
 * set as an own property: not every platform's `DOMException` constructor accepts it.
 * @param {number} reason
 * @returns {DOMException}
 */
export function sessionEndedError(reason) {
  const error = new DOMException('The session ended before the invocation completed.', 'AbortError');
  const cause = new NexusTransportError('The session ended before the invocation completed.', { reason });
  Object.defineProperty(error, 'cause', { value: cause, writable: true, configurable: true, enumerable: false });
  return error;
}

/**
 * @param {number} size
 * @param {number} limit
 * @returns {NexusMessageTooLargeError}
 */
function tooLarge(size, limit) {
  return new NexusMessageTooLargeError(`The message of ${size} bytes exceeds the negotiated maximum of ${limit} bytes.`);
}

/**
 * @param {number} reason A MessageAbort reason from the receiver.
 * @returns {NexusError}
 */
function refusedError(reason) {
  return reason === AbortReason.Busy
    ? new NexusBusyError('The peer has no room for the argument right now.')
    : new NexusMessageTooLargeError('The peer refused the argument as too large.');
}

/**
 * The error of an Exception result. The message mirrors C# `ProxyRemoteInvocationException`.
 * @param {import('./messages.js').RemoteErrorDetail | null} detail
 * @returns {NexusRemoteInvocationError}
 */
export function remoteInvocationError(detail) {
  let text = 'Exception occurred while executing method on proxy.';
  if (detail === null) return new NexusRemoteInvocationError(text);
  if (detail.message !== null) text += ` ${detail.type !== null ? `${detail.type}: ` : ''}${detail.message}`;
  if (detail.code !== null) text += ` (code ${detail.code})`;
  return new NexusRemoteInvocationError(text, { code: detail.code ?? undefined, remoteType: detail.type, remoteMessage: detail.message });
}
