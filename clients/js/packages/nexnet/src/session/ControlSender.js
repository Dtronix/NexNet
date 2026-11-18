/**
 * Coalesced control replies (C# `SessionControlSender`): ping replies, pipe window updates and close
 * acknowledgements, large-message aborts, invocation cancellations and invocation credit. Requests only record what
 * is owed; the frames are built when the write queue flushes, so any number of requests between two flushes cost at
 * most one frame of each kind (one per pipe, abort key or invocation), and credit rides along with other writes.
 * @module
 */

import { MessageType } from './protocol.js';
import { encodeFrame, encodeHeaderOnly } from './frames.js';
import { encodeInvocationCancellation, encodeInvocationCredit, encodeMessageAbort } from './wire.js';

/**
 * A pipe with control state to send (J08). The pipe queues itself at most once until drained.
 *
 * @typedef {object} PipeControlSource
 * @property {() => Uint8Array[]} takePendingControl Returns the frames to send now (window updates, state updates).
 */

export class ControlSender {
  #schedule;
  #pongPending = false;
  /** @type {Set<PipeControlSource>} */
  #pipes = new Set();
  /** @type {Map<string, { invocationId: number, kind: number, reason: number }>} */
  #aborts = new Map();
  /** @type {Set<number>} */
  #cancellations = new Set();
  #pendingCredit = 0;
  #creditThreshold;

  /**
   * @param {() => void} schedule Asks the write queue to flush.
   * @param {number} invocationCreditThreshold Credit is returned in batches of at least this much (half the advertised credits).
   */
  constructor(schedule, invocationCreditThreshold) {
    this.#schedule = schedule;
    this.#creditThreshold = Math.max(1, Math.floor(invocationCreditThreshold));
  }

  /** Requests a `Ping` reply. Requests before the next flush send one. @returns {void} */
  requestPong() {
    this.#pongPending = true;
    this.#schedule();
  }

  /**
   * Requests that a pipe's pending control state be sent.
   * @param {PipeControlSource} pipe
   * @returns {void}
   */
  requestPipeControl(pipe) {
    this.#pipes.add(pipe);
    this.#schedule();
  }

  /**
   * Requests a `MessageAbort`; at most one per (invocation ID, kind) is pending, the latest reason wins.
   * @param {number} invocationId @param {number} kind @param {number} reason
   * @returns {void}
   */
  requestAbort(invocationId, kind, reason) {
    this.#aborts.set(`${invocationId}:${kind}`, { invocationId, kind, reason });
    this.#schedule();
  }

  /**
   * Requests an `InvocationCancellation`; one per invocation ID.
   * @param {number} invocationId
   * @returns {void}
   */
  requestInvocationCancellation(invocationId) {
    this.#cancellations.add(invocationId);
    this.#schedule();
  }

  /**
   * Returns invocation credit to the peer. Sent once the pending credit reaches the threshold.
   * @param {number} [credits]
   * @returns {void}
   */
  returnInvocationCredit(credits = 1) {
    this.#pendingCredit += credits;
    if (this.#pendingCredit >= this.#creditThreshold) this.#schedule();
  }

  /** Invocation credit owed and not yet sent. @returns {number} */
  get pendingInvocationCredit() {
    return this.#pendingCredit;
  }

  /** True when {@link ControlSender#drain} has frames to return. @returns {boolean} */
  get hasPending() {
    return this.#pongPending
      || this.#pipes.size > 0
      || this.#aborts.size > 0
      || this.#cancellations.size > 0
      || this.#pendingCredit >= this.#creditThreshold;
  }

  /**
   * Takes every pending item as frames. Called by the write queue at flush time.
   * @returns {Uint8Array[]}
   */
  drain() {
    /** @type {Uint8Array[]} */
    const frames = [];
    if (this.#pongPending) {
      this.#pongPending = false;
      frames.push(encodeHeaderOnly(MessageType.Ping));
    }
    if (this.#pipes.size > 0) {
      const pipes = [...this.#pipes];
      this.#pipes.clear();
      for (const pipe of pipes) frames.push(...pipe.takePendingControl());
    }
    if (this.#aborts.size > 0) {
      for (const a of this.#aborts.values()) {
        frames.push(encodeFrame(MessageType.MessageAbort, encodeMessageAbort(a.invocationId, a.kind, a.reason)));
      }
      this.#aborts.clear();
    }
    if (this.#cancellations.size > 0) {
      for (const id of this.#cancellations) {
        frames.push(encodeFrame(MessageType.InvocationCancellation, encodeInvocationCancellation(id)));
      }
      this.#cancellations.clear();
    }
    if (this.#pendingCredit >= this.#creditThreshold) {
      let credit = this.#pendingCredit;
      this.#pendingCredit = 0;
      while (credit > 0) {
        const n = Math.min(credit, 0xffff);
        frames.push(encodeFrame(MessageType.InvocationCredit, encodeInvocationCredit(n)));
        credit -= n;
      }
    }
    return frames;
  }

  /** Drops everything pending (the session ended). @returns {void} */
  clear() {
    this.#pongPending = false;
    this.#pipes.clear();
    this.#aborts.clear();
    this.#cancellations.clear();
    this.#pendingCredit = 0;
  }
}
