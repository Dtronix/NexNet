/**
 * The session's write path: frames are queued and flushed once per microtask, in one `transport.write` call. While a
 * write is in flight (transport backpressure), new frames accumulate and go out in the next write once it resolves.
 * @module
 */

import { countSession } from './diagnostics.js';

export class WriteQueue {
  /** @type {import('../transport/ITransport.js').Transport} */
  #transport;
  /** @type {Uint8Array[]} */
  #pending = [];
  /** @type {Array<(written: boolean) => void>} */
  #waiters = [];
  #scheduled = false;
  #inFlight = false;
  #closed = false;
  #finishing = false;
  /** The batch being written: its frames stay counted as pending writes until it settles or the queue closes. */
  /** @type {{ count: number } | null} */
  #inFlightBatch = null;
  /** @type {() => Uint8Array[]} */
  #beforeFlush;
  /** @type {() => boolean} */
  #hasControl;
  /** @type {(error: unknown) => void} */
  #onError;
  /** @type {() => number} */
  #now;

  /** Clock time the write in flight started; meaningful only while {@link WriteQueue#inFlight}. Used by the progress timeout (§9). */
  flushStartedAt = 0;
  /** Clock time the last transport write completed; null until one has. @type {number | null} */
  lastSentAt = null;
  /** Frames and bytes handed to the transport. */
  framesSent = 0;
  bytesSent = 0;

  /**
   * @param {import('../transport/ITransport.js').Transport} transport
   * @param {object} options
   * @param {() => Uint8Array[]} options.beforeFlush Returns control frames to send with the queued frames.
   * @param {() => boolean} options.hasControl True when `beforeFlush` has frames to return.
   * @param {(error: unknown) => void} options.onError Called once when a transport write fails.
   * @param {() => number} options.now
   */
  constructor(transport, { beforeFlush, hasControl, onError, now }) {
    this.#transport = transport;
    this.#beforeFlush = beforeFlush;
    this.#hasControl = hasControl;
    this.#onError = onError;
    this.#now = now;
  }

  /** Frames queued and not yet handed to the transport. @returns {number} */
  get pendingCount() {
    return this.#pending.length;
  }

  /** True while a transport write is in flight. @returns {boolean} */
  get inFlight() {
    return this.#inFlight;
  }

  /**
   * Queues a frame. Resolves true once the transport accepted the write that carried it, false when the queue
   * closed first (the session ended) or the write failed.
   * @param {Uint8Array} frame
   * @returns {Promise<boolean>}
   */
  enqueue(frame) {
    if (this.#closed || this.#finishing) return Promise.resolve(false);
    this.#push(frame);
    const promise = new Promise((resolve) => this.#waiters.push(resolve));
    this.schedule();
    return /** @type {Promise<boolean>} */ (promise);
  }

  /** Requests a flush (for control frames, which are pulled through `beforeFlush`). @returns {void} */
  schedule() {
    if (this.#scheduled || this.#inFlight || this.#closed) return;
    this.#scheduled = true;
    queueMicrotask(() => {
      this.#scheduled = false;
      void this.#flush();
    });
  }

  /**
   * Queues `frame` as the last frame (a disconnect byte), refuses later ones, and resolves once it is written (true),
   * or false when the write failed or the queue closed.
   * @param {Uint8Array} frame
   * @returns {Promise<boolean>}
   */
  finish(frame) {
    const written = this.enqueue(frame);
    this.#finishing = true;
    return written;
  }

  /** Drops queued frames (their promises resolve false) and refuses new ones. @returns {void} */
  close() {
    if (this.#closed) return;
    this.#closed = true;
    countSession('pendingWrites', -this.#pending.length);
    this.#pending = [];
    // A write the transport never settles must not leak the counter.
    if (this.#inFlightBatch) {
      countSession('pendingWrites', -this.#inFlightBatch.count);
      this.#inFlightBatch.count = 0;
    }
    const waiters = this.#waiters;
    this.#waiters = [];
    for (const resolve of waiters) resolve(false);
  }

  /** @param {Uint8Array} frame */
  #push(frame) {
    this.#pending.push(frame);
    countSession('pendingWrites', 1);
  }

  async #flush() {
    if (this.#closed || this.#inFlight) return;
    if (!this.#finishing) {
      for (const frame of this.#beforeFlush()) this.#push(frame);
    }
    if (this.#pending.length === 0) return;

    const chunks = this.#pending;
    const waiters = this.#waiters;
    this.#pending = [];
    this.#waiters = [];
    this.#inFlight = true;
    const batch = { count: chunks.length };
    this.#inFlightBatch = batch;
    this.flushStartedAt = this.#now();
    let bytes = 0;
    for (const chunk of chunks) bytes += chunk.length;
    let ok = true;
    try {
      await this.#transport.write(chunks);
      this.framesSent += chunks.length;
      this.bytesSent += bytes;
      this.lastSentAt = this.#now();
    } catch (error) {
      ok = false;
      if (!this.#closed) this.#onError(error);
    } finally {
      this.#inFlight = false;
      this.#inFlightBatch = null;
      this.flushStartedAt = 0;
      countSession('pendingWrites', -batch.count);
    }
    for (const resolve of waiters) resolve(ok);
    if (!ok) {
      this.close();
      return;
    }
    if (this.#pending.length > 0 || (!this.#finishing && this.#hasControl())) this.schedule();
  }
}
