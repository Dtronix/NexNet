/**
 * The state of one duplex pipe (protocol spec §7, C# `NexusDuplexPipe`, `NexusPipeReader`, `NexusPipeWriter`): its
 * IDs, the state flags both sides exchange, the receive window and the credit granted back as the application
 * consumes, the send credit the peer granted, and the two-sided close. The public {@link NexusDuplexPipe} and the
 * channels wrap it; the {@link PipeManager} routes frames to it.
 *
 * Frames are handled synchronously in the transport's data callback; waiting readers and writers are woken through
 * promises, so application code never runs re-entrantly from there.
 * @module
 */

import { DisconnectReason } from '../contract/enums.js';
import { NexusBusyError, NexusError, NexusTransportError, cancellationError } from '../contract/errors.js';
import { sessionEndedError } from '../invocation/InvocationManager.js';
import { encodeFrame, encodePipeFrame, encodePipeWindowUpdate } from '../session/frames.js';
import { MessageType } from '../session/protocol.js';
import { encodeDuplexPipeUpdateState } from '../session/wire.js';
import { MAX_CREDIT, MAX_PIPE_CHUNK, PipeState, READER_COMPLETE, WRITER_COMPLETE } from './constants.js';
import { countPipes } from './diagnostics.js';
import { MetricTags } from '../metrics/instruments.js';

const REFUSED_REMOTE = Object.freeze({ [MetricTags.refusalSide]: 'remote' });
const RESET_TAGS = Object.freeze({
  invalid_item: Object.freeze({ [MetricTags.resetReason]: 'invalid_item' }),
  too_large: Object.freeze({ [MetricTags.resetReason]: 'too_large' }),
});

/**
 * Why a pipe was reset (the `nexnet.reset.reason` tag): a channel item that breaks the framing or does not decode,
 * or one larger than `maxMessageSize`.
 * @typedef {'invalid_item' | 'too_large'} PipeResetReason
 */

/**
 * What a pipe needs from its session, supplied by the {@link PipeManager}.
 *
 * @typedef {object} PipeHost
 * @property {(frame: Uint8Array) => Promise<boolean>} send Queues a frame after every frame queued before it.
 * @property {(core: PipeCore) => void} requestControl Asks the control sender to drain the pipe's window grant.
 * @property {(core: PipeCore) => void} changed Called after a state change, so a fully closed pipe is freed.
 * @property {() => number} now Session clock.
 * @property {import('../invocation/SessionBudget.js').SessionBudget} budget
 * @property {number} window This side's receive window per pipe (`pipeWindow`).
 * @property {() => number} sendMaxMessageSize Largest channel item the peer accepts.
 * @property {number} maxMessageSize Largest channel item this side accepts.
 * @property {import('../contract/codec.js').SerializerOptions} serializerOptions
 * @property {(level: import('../contract/config.js').NexusLogLevel, message: string, error?: unknown) => void} log
 * @property {(name: string, value: number, tags?: Readonly<Record<string, string>>) => void} metric Reports an event
 *   instrument (`session.recordMetric`).
 */

/**
 * Lifecycle of the opening side's pipe: `created` until passed to a pipe method, `invoked` until the peer's Ready or
 * Refused, then `open` or `failed`. A registered pipe starts `open`.
 * @typedef {'created' | 'invoked' | 'open' | 'failed'} PipePhase
 */

const noop = () => {};

export class PipeCore {
  /** The manager that owns the pipe. @type {import('./PipeManager.js').PipeManager} */
  manager;
  /** @type {PipeHost} */
  host;
  /** This side's local ID (1–255). */
  localId;
  /** The full ID (`clientId | serverId << 8`), or 0 until the peer's Ready arrives on the opening side. */
  id;
  /** True for a pipe this side opened (`createPipe`), false for one it registered. */
  initiating;
  /** @type {PipePhase} */
  phase;
  /** State flags (§4.6). @type {number} */
  state = PipeState.Unset;
  /** Local direction completions requested before the pipe was ready; sent once it is. */
  #deferred = 0;
  /** Window sent with the next state message (the registering side's Ready). */
  #readyWindow = 0;
  /** Why the pipe can't be used: refused, the invocation failed, or the session ended. @type {unknown} */
  failure = null;

  /** @type {Promise<void>} */
  ready;
  /** @type {Promise<void>} */
  completed;
  /** @type {((error: unknown) => void) | null} */
  #settleReady = null;
  /** @type {((error: unknown) => void) | null} */
  #settleCompleted = null;

  // Input (C# NexusPipeReader).
  /** @type {Uint8Array[]} */
  #chunks = [];
  #offset = 0;
  /** Bytes buffered and not consumed. */
  buffered = 0;
  /** Bytes still counted in the `bufferedBytes` diagnostic (zero once the session ended). */
  #counted = 0;
  /** True once no more data will be delivered: the peer's writer or this side's reader completed. */
  inputDone = false;
  /** Incremented whenever data arrives or the input completes; lets waiters skip data that arrived meanwhile. */
  inputVersion = 0;
  /** Bytes the peer may still send. */
  recvCredit = 0;
  /** Bytes consumed since credit was last granted back. */
  consumedSinceGrant = 0;
  /** Extra credit granted for an item larger than the window (§7.4), backed by a budget reservation. */
  extraOutstanding = 0;
  /** Clock time of the last data or extra grant. */
  lastDataAt = 0;
  /** True once data arrived or extra credit was granted, so {@link PipeCore#lastDataAt} is meaningful. */
  dataSeen = false;
  /** Length (prefix included) of a channel item that has partly arrived; 0 if none. */
  pendingItemLength = 0;
  /** @type {Array<() => void>} */
  #inputWaiters = [];

  // Output (C# NexusPipeWriter).
  /** Bytes this side may still send. */
  sendCredit = 0;
  /** True once this side may not send any more: its writer completed, or the peer's reader did. */
  outputDone = false;
  /** True once this side completed its own writer (so the writable is not errored for it). */
  localWriterDone = false;
  /** @type {Array<() => void>} */
  #creditWaiters = [];
  /** Called once when the output completes for a reason other than this side's writer. @type {(() => void) | null} */
  onOutputDone = null;
  /** Clock time the writer started waiting for credit; null when it is not waiting. @type {number | null} */
  #creditWaitSince = null;
  /** Bytes the writer still has to send while it waits for credit. */
  #waitingBytes = 0;

  // Two-sided close (§7.3).
  sentClose = false;
  receivedClose = false;
  released = false;
  freed = false;
  closingSince = 0;

  // Control state drained by the session's control sender: one pending grant counter per pipe.
  #pendingGrant = 0;
  #controlQueued = false;

  /**
   * @param {import('./PipeManager.js').PipeManager} manager
   * @param {PipeHost} host
   * @param {number} localId
   * @param {number} fullId 0 for a pipe this side opens.
   */
  constructor(manager, host, localId, fullId) {
    this.manager = manager;
    this.host = host;
    this.localId = localId;
    this.id = fullId;
    this.initiating = fullId === 0;
    this.phase = this.initiating ? 'created' : 'open';
    this.ready = new Promise((resolve, reject) => {
      this.#settleReady = (error) => (error === null ? resolve() : reject(error));
    });
    this.completed = new Promise((resolve, reject) => {
      this.#settleCompleted = (error) => (error === null ? resolve() : reject(error));
    });
    // Nobody may be awaiting them; a rejection is still delivered to whoever does.
    this.ready.catch(noop);
    this.completed.catch(noop);
  }

  /** True once this side sent its Complete, received the peer's, and the application released the pipe. */
  get isFullyClosed() {
    return this.sentClose && this.receivedClose && this.released;
  }

  /** @param {unknown} error null to resolve. */
  #resolveReady(error) {
    const settle = this.#settleReady;
    this.#settleReady = null;
    settle?.(error);
  }

  /** @param {unknown} error null to resolve. */
  #resolveCompleted(error) {
    const settle = this.#settleCompleted;
    this.#settleCompleted = null;
    settle?.(error);
  }

  // ---------------------------------------------------------------- state

  /**
   * Adds state flags (C# `UpdateState`). Completing a direction completes this side's reader or writer.
   * @param {number} flags
   * @returns {boolean} True when the state changed.
   */
  updateState(flags) {
    const current = this.state;
    if ((current & flags) === flags) return false;
    if (current === PipeState.Unset) {
      if (flags !== PipeState.Ready) return false;
      this.state = PipeState.Ready;
      this.phase = 'open';
      this.#resolveReady(null);
      return true;
    }
    if ((flags & READER_COMPLETE) !== 0 && (current & READER_COMPLETE) === 0) this.#completeInput();
    if ((flags & WRITER_COMPLETE) !== 0 && (current & WRITER_COMPLETE) === 0) this.#completeOutput();
    this.state |= flags;
    if ((this.state & PipeState.Complete) === PipeState.Complete) this.#resolveCompleted(null);
    return true;
  }

  /**
   * Sends the current state (C# `NotifyState`). Sending `Complete` is this side's close: sent once, after which
   * the pipe is closing until the peer's arrives.
   * @returns {Promise<void>}
   */
  async notifyState() {
    const current = this.state;
    if (current === PipeState.Complete) {
      if (this.sentClose) return;
      this.sentClose = true;
      this.closingSince = this.host.now();
    }
    const window = this.#readyWindow;
    this.#readyWindow = 0;
    const sent = this.host.send(encodeFrame(MessageType.DuplexPipeUpdateState, encodeDuplexPipeUpdateState(this.id, current, window)));
    this.host.changed(this);
    await sent;
  }

  /** Registering side: opens the receive window and sends Ready with it. @returns {Promise<void>} */
  sendReady() {
    this.recvCredit = this.host.window;
    this.#readyWindow = this.host.window;
    this.updateState(PipeState.Ready);
    return this.notifyState();
  }

  /**
   * Handles the peer's `DuplexPipeUpdateState` (C# `OnRemoteState`).
   * @param {number} state
   * @param {number} window
   * @returns {number} A disconnect reason, or 0.
   */
  onRemoteState(state, window) {
    if ((state & PipeState.Refused) !== 0) {
      // Only meaningful for a pipe this side opened and the peer has not acknowledged.
      if (this.state === PipeState.Unset) this.#onRefused();
      return 0;
    }
    if (window > 0 && !this.addSendCredit(window)) return DisconnectReason.FlowControl;

    const wasUnset = this.state === PipeState.Unset;
    this.updateState(state);

    // Opening side: the peer's Ready carried its window; grant ours in return (§7.1).
    if (wasUnset && (state & PipeState.Ready) !== 0) {
      this.recvCredit = this.host.window;
      this.grant(this.host.window);
      if (this.released) {
        // Disposed while the open was in flight: close at once.
        void this.close();
      } else if (this.#deferred !== 0) {
        const deferred = this.#deferred;
        this.#deferred = 0;
        if (this.updateState(deferred)) void this.notifyState();
      }
    }

    if (state === PipeState.Complete && !this.receivedClose) {
      this.receivedClose = true;
      // Answer the peer's close with ours so both sides can free the pipe.
      if (!this.sentClose) void this.close();
    }
    return 0;
  }

  /** Opening side: the peer refused the open (a limit, or the method is not authorized). */
  #onRefused() {
    this.host.metric('pipeRefusals', 1, REFUSED_REMOTE);
    this.phase = 'failed';
    this.failure = new NexusBusyError('The peer refused the pipe: a session limit is reached or the invocation is not authorized.');
    this.#resolveReady(this.failure);
    this.#resolveCompleted(null);
    this.markClosedWithoutExchange();
    this.#completeInput();
    this.#completeOutput();
  }

  /** The peer never opened the pipe, so there is no close to exchange. */
  markClosedWithoutExchange() {
    this.sentClose = true;
    this.receivedClose = true;
  }

  /**
   * The pipe method was not sent (canceled before send, not connected, too large): the pipe fails with `error`.
   * @param {unknown} error
   */
  invokeFailed(error) {
    this.phase = 'failed';
    this.failure = error;
    this.#resolveReady(error);
    this.#resolveCompleted(error);
    this.markClosedWithoutExchange();
    this.#completeInput();
    this.#completeOutput();
  }

  /**
   * The session ended (C# `CancelAll`). An open pipe completes both directions (buffered data stays readable); a
   * pipe that never opened fails as canceled, with the disconnect reason as the cause (amendment C-2).
   * @param {number} reason
   */
  sessionEnded(reason) {
    if (this.#counted > 0) countPipes('bufferedBytes', -this.#counted);
    this.#counted = 0;
    this.freed = true;
    this.failure ??= new NexusTransportError('The session ended.', { reason });
    if (this.state === PipeState.Unset) {
      if (this.phase !== 'failed') {
        const error = sessionEndedError(reason);
        this.#resolveReady(error);
        this.#resolveCompleted(error);
      }
      this.phase = 'failed';
      this.#completeInput();
      this.#completeOutput();
    } else {
      this.updateState(PipeState.Complete);
    }
    this.#wakeCredit();
  }

  /**
   * Sends this side's Complete if it has not been sent (C# `CloseAsync`).
   * @returns {Promise<void>}
   */
  close() {
    if (this.state === PipeState.Unset || this.sentClose) return Promise.resolve();
    this.updateState(PipeState.Complete);
    return this.notifyState();
  }

  /**
   * Completes both directions (`complete()`, C# `CompleteAsync`). Before the pipe is ready the completion is sent
   * once it is.
   * @returns {Promise<void>}
   */
  complete() {
    if (this.state === PipeState.Unset) {
      if (this.phase === 'created' || this.phase === 'invoked') this.#deferred |= PipeState.Complete;
      this.localWriterDone = true;
      this.#completeOutput();
      return Promise.resolve();
    }
    if (this.state === PipeState.Complete) return Promise.resolve();
    this.localWriterDone = true;
    return this.updateState(PipeState.Complete) ? this.notifyState() : Promise.resolve();
  }

  /**
   * This side's writer is done (half-close of the client-to-server direction).
   * @returns {Promise<void>}
   */
  completeWriting() {
    this.localWriterDone = true;
    if (this.state === PipeState.Unset) {
      if (this.phase === 'created' || this.phase === 'invoked') this.#deferred |= WRITER_COMPLETE;
      this.#completeOutput();
      return Promise.resolve();
    }
    return this.updateState(WRITER_COMPLETE) ? this.notifyState() : Promise.resolve();
  }

  /**
   * This side's reader is done: buffered data is dropped and data still arriving is discarded.
   * @returns {Promise<void>}
   */
  completeReading() {
    this.dropInput();
    this.#completeInput();
    if (this.state === PipeState.Unset) {
      if (this.phase === 'created' || this.phase === 'invoked') this.#deferred |= READER_COMPLETE;
      return Promise.resolve();
    }
    return this.updateState(READER_COMPLETE) ? this.notifyState() : Promise.resolve();
  }

  /**
   * Resets the pipe after a bad channel item (C# `Reset`): drops the input and closes the pipe. The session stays up.
   * @param {string} why
   * @param {PipeResetReason} [reason]
   * @returns {void}
   */
  reset(why, reason = 'invalid_item') {
    this.host.metric('pipeResets', 1, RESET_TAGS[reason]);
    this.host.log('debug', `Pipe P${this.id} reset: ${why}`);
    this.dropInput();
    this.#completeInput();
    void this.close();
  }

  /**
   * The application is done with the pipe (dispose): fails a pending `ready`, completes the directions and drops
   * buffered data. The manager sends Complete and frees the pipe.
   */
  dispose() {
    const disposed = new DOMException('The pipe was disposed.', 'AbortError');
    if (this.state === PipeState.Unset) {
      this.#resolveReady(disposed);
      this.#resolveCompleted(disposed);
    }
    this.localWriterDone = true;
    this.dropInput();
    this.#completeInput();
    this.#completeOutput();
  }

  // ---------------------------------------------------------------- input

  #completeInput() {
    if (this.inputDone) return;
    this.inputDone = true;
    this.#wakeInput();
  }

  #wakeInput() {
    this.inputVersion++;
    const waiters = this.#inputWaiters;
    if (waiters.length === 0) return;
    this.#inputWaiters = [];
    for (const wake of waiters) wake();
  }

  /**
   * Buffers data from the peer (C# `BufferData`). Never waits.
   * @param {Uint8Array} body The frame body; copied.
   * @returns {boolean} False when the data breaks the protocol: beyond the credit, or after the peer's Complete.
   */
  onData(body) {
    const length = body.length;
    if (this.receivedClose || length > this.recvCredit) return false;
    // Discarded data still used credit; it is not granted back, as the peer stops sending once it learns this side
    // completed.
    this.recvCredit -= length;
    if (this.inputDone || length === 0) return true;
    this.#chunks.push(body.slice());
    this.buffered += length;
    this.#counted += length;
    countPipes('bufferedBytes', length);
    this.lastDataAt = this.host.now();
    this.dataSeen = true;
    this.#wakeInput();
    return true;
  }

  /**
   * Waits until data arrives or the input completes after `version` (an earlier {@link PipeCore#inputVersion}).
   * Resolves at once when that already happened.
   * @param {AbortSignal | undefined} signal
   * @param {number} [version]
   * @returns {Promise<void>}
   */
  waitInput(signal, version = this.inputVersion) {
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    if (version !== this.inputVersion || this.inputDone) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const onAbort = () => {
        this.#inputWaiters = this.#inputWaiters.filter((w) => w !== wake);
        reject(cancellationError(signal));
      };
      const wake = () => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      };
      this.#inputWaiters.push(wake);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  /**
   * The first `n` buffered bytes (fewer if fewer are buffered), without consuming them. A view when they are in
   * one chunk, otherwise a copy.
   * @param {number} n
   * @returns {Uint8Array}
   */
  peek(n) {
    const count = Math.min(n, this.buffered);
    const first = this.#chunks[0];
    if (first === undefined) return new Uint8Array(0);
    if (first.length - this.#offset >= count) return first.subarray(this.#offset, this.#offset + count);
    const out = new Uint8Array(count);
    let written = 0;
    for (let i = 0; written < count; i++) {
      const chunk = this.#chunks[i];
      const start = i === 0 ? this.#offset : 0;
      const take = Math.min(chunk.length - start, count - written);
      out.set(chunk.subarray(start, start + take), written);
      written += take;
    }
    return out;
  }

  /**
   * The rest of the first buffered chunk, without consuming it.
   * @returns {Uint8Array}
   */
  peekChunk() {
    const first = this.#chunks[0];
    return first === undefined ? new Uint8Array(0) : first.subarray(this.#offset);
  }

  /**
   * Removes and returns the rest of the first buffered chunk. The chunk's buffer is not used again by the pipe, so
   * it may be transferred (a byte stream's `enqueue`).
   * @returns {Uint8Array}
   */
  takeChunk() {
    const chunk = /** @type {Uint8Array} */ (this.#chunks.shift());
    const view = this.#offset === 0 ? chunk : chunk.subarray(this.#offset);
    this.#offset = 0;
    this.#released(view.length);
    return view;
  }

  /**
   * Consumes `n` buffered bytes; credit is granted back as they are (§7.2).
   * @param {number} n
   */
  consume(n) {
    let left = n;
    while (left > 0) {
      const first = this.#chunks[0];
      const available = first.length - this.#offset;
      if (available > left) {
        this.#offset += left;
        break;
      }
      left -= available;
      this.#chunks.shift();
      this.#offset = 0;
    }
    this.#released(n);
  }

  /** @param {number} n Bytes removed from the buffer by the reader. */
  #released(n) {
    this.buffered -= n;
    const counted = Math.min(n, this.#counted);
    this.#counted -= counted;
    if (counted > 0) countPipes('bufferedBytes', -counted);
    this.#onConsumed(n);
  }

  /** Drops everything buffered (the reader completed or the pipe was disposed). Grants nothing back. */
  dropInput() {
    this.#chunks = [];
    this.#offset = 0;
    this.buffered = 0;
    if (this.#counted > 0) countPipes('bufferedBytes', -this.#counted);
    this.#counted = 0;
    this.pendingItemLength = 0;
  }

  /**
   * Records consumed bytes: they pay off extra credit first (not granted back), then are granted back once at least
   * half the window has been consumed since the last grant (C# `OnConsumed`).
   * @param {number} released
   */
  #onConsumed(released) {
    if (released <= 0) return;
    let left = released;
    if (this.extraOutstanding > 0) {
      const paid = Math.min(left, this.extraOutstanding);
      this.extraOutstanding -= paid;
      left -= paid;
      if (!this.freed) this.host.budget.release(paid);
    }
    if (left <= 0 || this.inputDone) return;
    this.consumedSinceGrant += left;
    if (this.consumedSinceGrant < this.host.window / 2) return;
    this.#grantConsumed();
  }

  #grantConsumed() {
    const grant = this.consumedSinceGrant;
    this.consumedSinceGrant = 0;
    this.recvCredit += grant;
    this.grant(grant);
  }

  /**
   * Makes sure the writer can send `missing` more bytes: if the credit it still holds is less, grants everything
   * consumed but not yet granted back, without waiting for half the window (§7.2, flow-control.md §12).
   * @param {number} missing
   */
  ensureCredit(missing) {
    if (this.recvCredit >= missing || this.consumedSinceGrant <= 0 || this.inputDone) return;
    this.#grantConsumed();
  }

  /**
   * Makes room for an item larger than the window (§7.4): reserves `extra` bytes from the session budget, all at
   * once (waiting in FIFO order until it fits), and grants them as extra credit. Does nothing while an earlier extra
   * grant is still outstanding. Resolves false when the wait was canceled or the session ended.
   * @param {number} extra
   * @param {AbortSignal} [signal]
   * @returns {Promise<boolean>}
   */
  async grantExtra(extra, signal) {
    if (this.extraOutstanding > 0) return true;
    if (!(await this.host.budget.reserve(extra, signal))) return false;
    if (this.freed || this.inputDone) {
      if (!this.freed) this.host.budget.release(extra);
      return false;
    }
    this.extraOutstanding += extra;
    this.recvCredit += extra;
    this.lastDataAt = this.host.now();
    this.dataSeen = true;
    this.grant(extra);
    return true;
  }

  /**
   * Queues a window grant; grants are coalesced until the control sender drains them.
   * @param {number} bytes
   */
  grant(bytes) {
    this.#pendingGrant += bytes;
    if (this.#controlQueued) return;
    this.#controlQueued = true;
    this.host.requestControl(this);
  }

  /**
   * The pending window grant as frames (`PipeControlSource`). Called by the control sender at flush time.
   * @returns {Uint8Array[]}
   */
  takePendingControl() {
    this.#controlQueued = false;
    const grant = this.#pendingGrant;
    this.#pendingGrant = 0;
    if (grant <= 0 || this.sentClose || this.freed) return [];
    return [encodePipeWindowUpdate(this.id, grant)];
  }

  /**
   * Describes how this pipe stalled for the log, or null: closing without the peer's confirmation, or an item given
   * extra credit whose data stopped arriving (§9).
   * @param {number} now
   * @param {number} timeout
   * @returns {string | null}
   */
  describeStall(now, timeout) {
    if (this.sentClose && !this.receivedClose && now - this.closingSince > timeout) {
      return `pipe P${this.id} closing, the peer has not confirmed for ${Math.round(now - this.closingSince)} ms`;
    }
    if (this.extraOutstanding > 0 && !this.inputDone && now - this.lastDataAt > timeout) {
      return `pipe P${this.id} item of ${this.pendingItemLength} bytes, no data for ${Math.round(now - this.lastDataAt)} ms`;
    }
    return null;
  }

  // ---------------------------------------------------------------- output

  #completeOutput() {
    if (this.outputDone) return;
    this.outputDone = true;
    this.#wakeCredit();
    if (!this.localWriterDone) this.onOutputDone?.();
  }

  #wakeCredit() {
    const waiters = this.#creditWaiters;
    if (waiters.length === 0) return;
    this.#creditWaiters = [];
    for (const wake of waiters) wake();
  }

  /**
   * Adds send credit granted by the peer.
   * @param {number} bytes
   * @returns {boolean} False when the total would exceed 2^31 (a `FlowControl` violation).
   */
  addSendCredit(bytes) {
    if (this.sendCredit + bytes > MAX_CREDIT) return false;
    this.sendCredit += bytes;
    if (bytes > 0) this.#wakeCredit();
    return true;
  }

  /**
   * Waits until send credit is available or the output completes. Waiting for credit is never a stall (§9). A wait
   * that parks is shown in snapshots (`waitingForCredit`, `waitingBytes`) and recorded to
   * `nexnet.pipe.credit_wait` when it ends.
   * @param {AbortSignal | undefined} signal
   * @param {number} [waitingBytes] Bytes the writer still has to send.
   * @returns {Promise<void>}
   */
  waitCredit(signal, waitingBytes = 0) {
    if (signal?.aborted) return Promise.reject(cancellationError(signal));
    if (this.sendCredit > 0 || this.outputDone) return Promise.resolve();
    if (this.#creditWaitSince === null) this.#creditWaitSince = this.host.now();
    this.#waitingBytes = waitingBytes;
    return new Promise((resolve, reject) => {
      const onAbort = () => {
        this.#creditWaiters = this.#creditWaiters.filter((w) => w !== wake);
        this.#endCreditWait();
        reject(cancellationError(signal));
      };
      const wake = () => {
        signal?.removeEventListener('abort', onAbort);
        this.#endCreditWait();
        resolve();
      };
      this.#creditWaiters.push(wake);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  /** Ends the credit wait in progress, if any, and records how long it lasted (C# `EndCreditWait`). */
  #endCreditWait() {
    const since = this.#creditWaitSince;
    if (since === null) return;
    this.#creditWaitSince = null;
    this.#waitingBytes = 0;
    this.host.metric('pipeCreditWait', Math.max(0, this.host.now() - since) / 1000);
  }

  /**
   * A snapshot of the pipe (C# `NexusDuplexPipe.GetSnapshot`). Durations are milliseconds on the session clock.
   * @param {number} now
   * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot}
   */
  snapshot(now) {
    const since = (/** @type {number} */ at) => Math.max(0, now - at);
    return {
      id: this.id !== 0 ? this.id : this.localId,
      isReady: (this.state & PipeState.Ready) !== 0,
      readerCompleted: this.inputDone,
      writerCompleted: this.outputDone,
      closing: this.sentClose && !this.receivedClose ? since(this.closingSince) : null,
      window: this.host.window,
      receiveCredit: this.recvCredit,
      buffered: this.buffered,
      consumedNotGranted: this.consumedSinceGrant,
      extraOutstanding: this.extraOutstanding,
      sinceLastData: this.dataSeen ? since(this.lastDataAt) : null,
      partialItem: this.pendingItemLength > 0
        ? { length: this.pendingItemLength, received: Math.min(this.buffered, this.pendingItemLength) }
        : null,
      sendCredit: this.sendCredit,
      waitingForCredit: this.#creditWaitSince === null ? null : since(this.#creditWaitSince),
      waitingBytes: this.#waitingBytes,
    };
  }

  /** The error of a write that can't be sent. @returns {unknown} */
  outputClosedError() {
    if (this.failure !== null) return this.failure;
    return new NexusError('The pipe\'s writing direction is complete.');
  }

  /**
   * Sends `bytes` as `DuplexPipeWrite` frames, split at the credit and frame boundaries, waiting for credit in
   * between. Resolves once the last frame was written to the transport. Callers serialize their writes.
   *
   * @param {Uint8Array} bytes
   * @param {AbortSignal} [signal] Cancels a wait for credit.
   * @param {boolean} [framed] Channel items: an abort after part of `bytes` was sent completes the writing direction,
   *   since the item stream can't be resumed.
   * @returns {Promise<void>}
   */
  async writeBytes(bytes, signal, framed = false) {
    let offset = 0;
    /** @type {Promise<boolean> | null} */
    let last = null;
    while (offset < bytes.length) {
      if (this.outputDone) throw this.outputClosedError();
      if (this.sendCredit <= 0) {
        try {
          await this.waitCredit(signal, bytes.length - offset);
        } catch (error) {
          if (framed && offset > 0) void this.completeWriting();
          throw error;
        }
        continue;
      }
      const n = Math.min(bytes.length - offset, this.sendCredit, MAX_PIPE_CHUNK);
      this.sendCredit -= n;
      last = this.host.send(encodePipeFrame(MessageType.DuplexPipeWrite, this.id, bytes.subarray(offset, offset + n)));
      offset += n;
    }
    if (last !== null && !(await last)) throw this.outputClosedError();
  }
}
