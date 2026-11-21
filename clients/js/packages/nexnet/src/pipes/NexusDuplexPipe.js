/**
 * The public duplex pipe (C# `INexusDuplexPipe`) over a {@link PipeCore}.
 * @module
 */

import { NexusError } from '../contract/errors.js';
import { bridgePipes, forwardPipe } from './forwarding.js';

/** @type {WeakMap<object, import('./PipeCore.js').PipeCore>} */
const cores = new WeakMap();

/**
 * The state behind a pipe the runtime created. Internal to the runtime.
 * @param {NexusDuplexPipe} pipe
 * @returns {import('./PipeCore.js').PipeCore}
 */
export function pipeCore(pipe) {
  const core = cores.get(pipe);
  if (!core) throw new TypeError('Not a pipe created by the NexNet runtime (use client.createPipe()).');
  return core;
}

/**
 * Converts a written chunk to bytes.
 * @param {unknown} chunk
 * @returns {Uint8Array}
 */
function toBytes(chunk) {
  if (chunk instanceof Uint8Array) return chunk;
  if (ArrayBuffer.isView(chunk)) return new Uint8Array(chunk.buffer, chunk.byteOffset, chunk.byteLength);
  if (chunk instanceof ArrayBuffer) return new Uint8Array(chunk);
  throw new TypeError('A pipe accepts Uint8Array, ArrayBufferView or ArrayBuffer chunks.');
}

/**
 * A duplex byte pipe. Mirrors C# `INexusDuplexPipe`. Created by `client.createPipe()` (passed to a pipe method of
 * the server proxy) or by the runtime for a pipe parameter of a client nexus method.
 *
 * - `readable` delivers the bytes the server writes. Credit is granted to the server only as they are read (half the
 *   window at a time), so a reader that stops reading stops the server's writer, never the session.
 * - `writable` sends bytes, split into frames at the credit the server granted; a write waits for credit. Closing it
 *   half-closes the pipe (the client-to-server direction); aborting it does the same.
 * - `complete()` completes both directions. `[Symbol.asyncDispose]` releases the pipe: it sends the close if needed
 *   and frees the pipe ID and window once the server closed too. A pipe a client nexus method received is released
 *   when the method returns.
 *
 * Use either `readable` or a channel reader on one pipe, not both; the same goes for `writable` and a channel writer.
 */
export class NexusDuplexPipe {
  /** @type {ReadableStream<Uint8Array> | null} */
  #readable = null;
  /** @type {WritableStream<Uint8Array> | null} */
  #writable = null;

  /**
   * Created by the runtime.
   * @param {import('./PipeCore.js').PipeCore} core
   */
  constructor(core) {
    if (!core || typeof core !== 'object') throw new TypeError('Pipes are created by the runtime: use client.createPipe().');
    cores.set(this, core);
  }

  /** Local pipe ID (1–255), or 0 while unassigned. @returns {number} */
  get id() {
    return pipeCore(this).localId;
  }

  /**
   * Incoming bytes: a byte stream (`type: 'bytes'`, BYOB readers supported) with `highWaterMark` 0, so nothing is
   * pulled, and no credit granted, before a read. Cancelling it completes this side's reading direction.
   * @returns {ReadableStream<Uint8Array>}
   */
  get readable() {
    if (this.#readable === null) {
      const core = pipeCore(this);
      let canceled = false;
      this.#readable = /** @type {ReadableStream<Uint8Array>} */ (new ReadableStream({
        type: 'bytes',
        pull: async (controller) => {
          let version = core.inputVersion;
          while (core.buffered === 0) {
            if (core.inputDone) {
              if (canceled) return;
              controller.close();
              controller.byobRequest?.respond(0);
              return;
            }
            await core.waitInput(undefined, version);
            version = core.inputVersion;
          }
          if (canceled) return;
          const request = controller.byobRequest;
          const view = request?.view;
          if (request && view) {
            const target = new Uint8Array(view.buffer, view.byteOffset, view.byteLength);
            const source = core.peekChunk();
            const n = Math.min(target.length, source.length);
            target.set(source.subarray(0, n));
            core.consume(n);
            request.respond(n);
          } else {
            // The chunk owns its buffer (a copy of the frame body), so it can be transferred.
            controller.enqueue(/** @type {Uint8Array<ArrayBuffer>} */ (core.takeChunk()));
          }
        },
        cancel: async () => {
          canceled = true;
          await core.completeReading();
        },
      }, { highWaterMark: 0 }));
    }
    return this.#readable;
  }

  /**
   * Outgoing bytes. A write resolves once its bytes were handed to the transport, waiting for credit as needed.
   * `highWaterMark` is one chunk: with 0, `ReadableStream.pipeTo` would never write (it waits for a positive
   * `desiredSize`); the sink still takes one chunk at a time and sends only what the credit allows. Closing or
   * aborting it half-closes the pipe. It errors when the server completes its reading direction or the pipe fails.
   * @returns {WritableStream<Uint8Array>}
   */
  get writable() {
    if (this.#writable === null) {
      const core = pipeCore(this);
      this.#writable = new WritableStream({
        start: (controller) => {
          core.onOutputDone = () => controller.error(core.outputClosedError());
          if (core.outputDone && !core.localWriterDone) controller.error(core.outputClosedError());
        },
        write: (chunk, controller) => core.writeBytes(toBytes(chunk), controller.signal),
        close: () => core.completeWriting(),
        abort: () => core.completeWriting(),
      }, { highWaterMark: 1 });
    }
    return this.#writable;
  }

  /**
   * Resolves when both sides are ready. Rejects with `NexusBusyError` when the server refused the pipe (a limit or
   * authorization), with the invocation's error when the pipe method could not be sent, and with an `AbortError`
   * when the pipe was disposed or the session ended first.
   * @returns {Promise<void>}
   */
  get ready() {
    return pipeCore(this).ready;
  }

  /** Resolves when both directions are complete. @returns {Promise<void>} */
  get completed() {
    return pipeCore(this).completed;
  }

  /** Completes both directions. @returns {Promise<void>} */
  async complete() {
    await pipeCore(this).complete();
  }

  /**
   * Copies everything this pipe receives to `destination` until this pipe's input completes, then half-closes
   * `destination` (completes its writing direction). This pipe is never completed. A forward reads one buffer at a
   * time and waits for the destination's credit before reading again, so a slow destination stalls this pipe's
   * writer instead of buffering. Mirrors C# `ForwardToAsync`. Do not read this pipe or write `destination` meanwhile.
   *
   * @param {NexusDuplexPipe} destination
   * @param {{ signal?: AbortSignal }} [options] Stops the forward; neither pipe is completed.
   * @returns {Promise<void>} Resolves when this pipe's input completed or `destination`'s output was completed.
   */
  forwardTo(destination, options) {
    if (destination === this) return Promise.reject(new NexusError('A pipe can not be forwarded to itself.'));
    return forwardPipe(pipeCore(this), pipeCore(destination), options?.signal);
  }

  /**
   * Forwards this pipe to `other` and `other` to this pipe; see {@link NexusDuplexPipe.bridge}.
   * @param {NexusDuplexPipe} other
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  bridge(other, options) {
    return NexusDuplexPipe.bridge(this, other, options);
  }

  /**
   * Forwards `a` to `b` and `b` to `a` concurrently (C# `BridgeAsync`). Each direction half-closes its destination
   * when its source completes; the bridge resolves once both directions finished. If either pipe ends abnormally
   * (reset, disconnect, `complete()`) while a direction still runs, both pipes are completed and the bridge resolves.
   * A direction that fails completes both pipes and rejects with its error.
   *
   * @param {NexusDuplexPipe} a
   * @param {NexusDuplexPipe} b
   * @param {{ signal?: AbortSignal }} [options] Stops both directions without completing either pipe; rejects with
   *   the abort reason.
   * @returns {Promise<void>}
   */
  static bridge(a, b, options) {
    if (a === b) return Promise.reject(new NexusError('A pipe can not be bridged to itself.'));
    return bridgePipes(a, b, pipeCore(a), pipeCore(b), options?.signal);
  }

  /**
   * Releases the pipe: sends the close if needed, fails a pending `ready`, and frees the pipe ID and receive window
   * once the server closed too.
   * @returns {Promise<void>}
   */
  [Symbol.asyncDispose]() {
    const core = pipeCore(this);
    return core.manager.release(core);
  }
}
