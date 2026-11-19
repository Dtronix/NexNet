/**
 * Reassembles large arguments and results the peer streams to this side (protocol spec §6.2–§6.4, C#
 * `LargeMessageReceiver`). Called from the session's data callback only.
 *
 * A stream is keyed by (invocation ID, kind). Its total length is reserved from the session budget at Start; chunks
 * are copied as they arrive. A stream that can't be reserved is aborted with Busy and its chunks are discarded up to
 * the total length. On completion the reservation passes to the caller with the reassembled body.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';
import { MessageType } from '../session/protocol.js';
import { countInvocation } from './diagnostics.js';
import { AbortReason, LargeMessageKind } from './messages.js';

/**
 * What the receiver needs from its session.
 *
 * @typedef {object} LargeMessageOwner
 * @property {(invocationId: number) => boolean} isCallOutstanding True if this side waits for the call's result.
 * @property {() => boolean} tryTakeInvocationCredit Takes one inbound credit for a large argument; false when none is left.
 * @property {() => void} returnInvocationCredit Returns a credit taken for an argument that will not be queued.
 * @property {(invocationId: number, kind: number, reason: number) => void} sendAbort Queues a MessageAbort (never waits).
 */

/**
 * A reassembled message.
 *
 * @typedef {object} CompletedMessage
 * @property {number} innerType 110 (Invocation) or 112 (InvocationResult).
 * @property {Uint8Array} body The inner message's body.
 * @property {number} reserved Budget bytes the caller now holds and must release once the message is processed.
 */

/**
 * @typedef {object} Stream
 * @property {number} total
 * @property {number} innerType
 * @property {number} kind
 * @property {number} received
 * @property {number} lastProgress
 * @property {boolean} aborting
 * @property {boolean} reserved
 * @property {Uint8Array[]} chunks
 */

export class LargeMessageReceiver {
  /** @type {Map<number, Stream>} */
  #streams = new Map();
  #owner;
  #budget;
  #maxMessageSize;
  #now;

  /**
   * @param {LargeMessageOwner} owner
   * @param {import('./SessionBudget.js').SessionBudget} budget
   * @param {number} maxMessageSize This side's advertised `maxMessageSize`.
   * @param {() => number} now Clock time.
   */
  constructor(owner, budget, maxMessageSize, now) {
    this.#owner = owner;
    this.#budget = budget;
    this.#maxMessageSize = maxMessageSize;
    this.#now = now;
  }

  /** Streams being received. @returns {number} */
  get count() {
    return this.#streams.size;
  }

  /** @param {number} invocationId @param {number} kind */
  static #key(invocationId, kind) {
    return invocationId | (kind << 16);
  }

  /**
   * Handles a MessageStart. Returns a disconnect reason when it breaks the protocol, else 0.
   * @param {import('./messages.js').MessageStart} start
   * @returns {number}
   */
  onStart(start) {
    const expectedInner = start.kind === LargeMessageKind.Argument ? MessageType.Invocation
      : start.kind === LargeMessageKind.Result ? MessageType.InvocationResult : -1;
    if (expectedInner < 0 || start.innerType !== expectedInner || start.totalLength === 0) return DisconnectReason.ProtocolError;
    if (start.totalLength > this.#maxMessageSize) return DisconnectReason.ProtocolError;

    const key = LargeMessageReceiver.#key(start.invocationId, start.kind);
    if (this.#streams.has(key)) return DisconnectReason.ProtocolError;
    if (start.kind === LargeMessageKind.Result && !this.#owner.isCallOutstanding(start.invocationId)) return DisconnectReason.ProtocolError;

    // A large argument uses the invocation's credit.
    if (start.kind === LargeMessageKind.Argument && !this.#owner.tryTakeInvocationCredit()) return DisconnectReason.FlowControl;

    /** @type {Stream} */
    const stream = {
      total: start.totalLength,
      innerType: start.innerType,
      kind: start.kind,
      received: 0,
      lastProgress: this.#now(),
      aborting: false,
      reserved: false,
      chunks: [],
    };
    if (this.#budget.tryReserve(start.totalLength)) {
      stream.reserved = true;
    } else {
      // No room now: refuse it and discard its chunks. The argument will not be queued, so its credit returns.
      stream.aborting = true;
      if (start.kind === LargeMessageKind.Argument) this.#owner.returnInvocationCredit();
      this.#owner.sendAbort(start.invocationId, start.kind, AbortReason.Busy);
    }
    this.#streams.set(key, stream);
    countInvocation('largeMessages', 1);
    return 0;
  }

  /**
   * Handles a MessageChunk. `payload` is copied.
   * @param {number} invocationId
   * @param {number} kind
   * @param {Uint8Array} payload
   * @returns {{ reason: number, completed: CompletedMessage | null }}
   */
  onChunk(invocationId, kind, payload) {
    const key = LargeMessageReceiver.#key(invocationId, kind);
    const stream = this.#streams.get(key);
    if (!stream || stream.received + payload.length > stream.total) return { reason: DisconnectReason.ProtocolError, completed: null };

    stream.received += payload.length;
    stream.lastProgress = this.#now();
    if (stream.aborting) {
      // Discarded, up to the total length; then the key closes.
      if (stream.received === stream.total) this.#remove(key);
      return { reason: 0, completed: null };
    }

    stream.chunks.push(payload.slice());
    if (stream.received < stream.total) return { reason: 0, completed: null };

    this.#remove(key);
    const body = new Uint8Array(stream.total);
    let offset = 0;
    for (const chunk of stream.chunks) {
      body.set(chunk, offset);
      offset += chunk.length;
    }
    stream.chunks = [];
    stream.reserved = false;
    return { reason: 0, completed: { innerType: stream.innerType, body, reserved: stream.total } };
  }

  /**
   * Handles an abort from the stream's sender: Cancelled ends the stream, Ack ends a stream this side refused. An
   * abort for an unknown key is ignored, as it can race with completion.
   * @param {{ invocationId: number, kind: number, reason: number }} abort
   * @returns {void}
   */
  onAbort(abort) {
    const key = LargeMessageReceiver.#key(abort.invocationId, abort.kind);
    const stream = this.#streams.get(key);
    if (!stream) return;
    if (abort.reason === AbortReason.Ack && !stream.aborting) return;
    this.#remove(key);
    // An argument this side had accepted will not be queued now.
    if (stream.kind === LargeMessageKind.Argument && !stream.aborting) this.#owner.returnInvocationCredit();
    this.#close(stream);
  }

  /**
   * Describes a stream that received nothing for longer than `timeout` (§9); null when none did.
   * @param {number} now
   * @param {number} timeout
   * @returns {string | null}
   */
  findStalled(now, timeout) {
    for (const [key, stream] of this.#streams) {
      const idle = now - stream.lastProgress;
      if (idle > timeout) {
        return `inbound large ${stream.kind === LargeMessageKind.Argument ? 'argument' : 'result'} of invocation ${key & 0xffff}, `
          + `${stream.received} of ${stream.total} bytes, no data for ${Math.round(idle)} ms`;
      }
    }
    return null;
  }

  /** Drops every stream (the session ended). @returns {void} */
  clear() {
    for (const [key, stream] of [...this.#streams]) {
      this.#remove(key);
      this.#close(stream);
    }
  }

  /** @param {number} key */
  #remove(key) {
    if (this.#streams.delete(key)) countInvocation('largeMessages', -1);
  }

  /** @param {Stream} stream */
  #close(stream) {
    stream.chunks = [];
    if (stream.reserved) {
      stream.reserved = false;
      this.#budget.release(stream.total);
    }
  }
}
