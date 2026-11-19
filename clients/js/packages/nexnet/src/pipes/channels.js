/**
 * Typed channels over a duplex pipe (protocol spec §7.4, C# `NexusDuplexChannel`, `NexusChannelReader`,
 * `NexusChannelWriter`): each item is one MessagePack value preceded by its length as a varint.
 * @module
 */

import { NexusError, NexusMessageTooLargeError, cancellationError } from '../contract/errors.js';
import { invocationCodec } from '../invocation/codec.js';
import { ChannelFramingError, MAX_PREFIX_LENGTH, prefixLength, readLength, writeLength } from './framing.js';
import { pipeCore } from './NexusDuplexPipe.js';

/**
 * @template T
 * @typedef {{ kind: 'item', value: T } | { kind: 'none' } | { kind: 'partial', length: number, missing: number }} Decoded
 */

/** @type {{ kind: 'none' }} */
const NONE = Object.freeze({ kind: 'none' });

/**
 * Reads typed items from a channel. Mirrors C# `INexusChannelReader<T>`. Get one from
 * {@link NexusDuplexChannel#getReader}.
 *
 * An item is consumed (and its bytes granted back to the writer) when it is read. An item that breaks the framing (a
 * length over `maxMessageSize`, a malformed prefix, an item that does not decode to exactly its length) resets the
 * pipe and rejects the read; the session stays up. An item larger than the pipe's window gets extra credit from the
 * session budget while it arrives.
 *
 * @template T
 */
export class NexusChannelReader {
  /** @type {import('./PipeCore.js').PipeCore} */
  #core;
  /** @type {import('../contract/codec.js').Formatter<T>} */
  #formatter;
  /** Reads run one at a time. @type {Promise<unknown>} */
  #queue = Promise.resolve();

  /**
   * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
   * @param {import('../contract/codec.js').Formatter<T>} formatter
   */
  constructor(pipe, formatter) {
    this.#core = pipeCore(pipe);
    this.#formatter = formatter;
  }

  /** True once the writer completed and every item was read. @returns {boolean} */
  get isComplete() {
    return this.#core.inputDone && this.#core.buffered === 0;
  }

  /**
   * Reads one item.
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<IteratorResult<T, undefined>>}
   */
  read(options) {
    const signal = options?.signal;
    return this.#serialize(async () => {
      const items = await this.#readItems(1, signal);
      return items.length > 0 ? { value: items[0], done: false } : { value: undefined, done: true };
    });
  }

  /**
   * Reads up to `max` items that are available, waiting for at least one. Empty once complete.
   * @param {number} max
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<T[]>}
   */
  readBatch(max, options) {
    if (!Number.isInteger(max) || max < 1) return Promise.reject(new RangeError('max must be a positive integer'));
    const signal = options?.signal;
    return this.#serialize(() => this.#readItems(max, signal));
  }

  /**
   * Reads every remaining item.
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<T[]>}
   */
  readUntilComplete(options) {
    const signal = options?.signal;
    return this.#serialize(async () => {
      /** @type {T[]} */
      const all = [];
      for (;;) {
        const items = await this.#readItems(Number.MAX_SAFE_INTEGER, signal);
        if (items.length === 0) return all;
        for (const item of items) all.push(item);
      }
    });
  }

  /** @returns {AsyncGenerator<T, void, undefined>} */
  async *[Symbol.asyncIterator]() {
    for (;;) {
      const result = await this.read();
      if (result.done) return;
      yield result.value;
    }
  }

  /**
   * @template R
   * @param {() => Promise<R>} run
   * @returns {Promise<R>}
   */
  #serialize(run) {
    const result = this.#queue.then(run);
    this.#queue = result.catch(() => {});
    return result;
  }

  /**
   * Waits for at least one item, then decodes up to `max` items already buffered. Empty once complete.
   * @param {number} max
   * @param {AbortSignal | undefined} signal
   * @returns {Promise<T[]>}
   */
  async #readItems(max, signal) {
    const core = this.#core;
    /** @type {T[]} */
    const items = [];
    for (;;) {
      if (signal?.aborted) throw cancellationError(signal);
      const version = core.inputVersion;
      /** @type {Decoded<T>} */
      let decoded = NONE;
      while (items.length < max) {
        decoded = this.#decodeOne();
        if (decoded.kind !== 'item') break;
        items.push(decoded.value);
      }
      if (items.length > 0) return items;

      if (core.inputDone) {
        // The writer completed inside an item: that item never arrives.
        if (core.buffered > 0) core.dropInput();
        return items;
      }
      if (decoded.kind === 'partial') {
        // Waiting for the rest of an item: make sure the writer has the credit to send it.
        core.ensureCredit(decoded.missing);
        const window = core.host.window;
        if (decoded.length > window && !(await core.grantExtra(decoded.length - window, signal))) {
          if (signal?.aborted) throw cancellationError(signal);
        }
      } else if (core.buffered > 0) {
        // An incomplete prefix.
        core.ensureCredit(MAX_PREFIX_LENGTH);
      }
      await core.waitInput(signal, version);
    }
  }

  /**
   * Decodes the item at the start of the buffer, if it has fully arrived.
   * @returns {Decoded<T>}
   */
  #decodeOne() {
    const core = this.#core;
    if (core.buffered === 0) {
      core.pendingItemLength = 0;
      return NONE;
    }
    /** @type {{ length: number, size: number } | null} */
    let prefix;
    try {
      prefix = readLength(core.peek(MAX_PREFIX_LENGTH));
    } catch (error) {
      core.reset('malformed item prefix');
      throw new NexusError(/** @type {Error} */ (error).message, { cause: error });
    }
    if (prefix === null) return NONE;
    const limit = core.host.maxMessageSize;
    if (prefix.length > limit) {
      core.reset(`item of ${prefix.length} bytes exceeds maxMessageSize`, 'too_large');
      throw new NexusMessageTooLargeError(`The channel item of ${prefix.length} bytes exceeds the maximum of ${limit} bytes.`);
    }
    const total = prefix.size + prefix.length;
    if (core.buffered < total) {
      core.pendingItemLength = total;
      return { kind: 'partial', length: total, missing: total - core.buffered };
    }
    const bytes = core.peek(total).subarray(prefix.size);
    /** @type {T} */
    let value;
    try {
      const reader = invocationCodec().createReader(bytes, core.host.serializerOptions);
      value = this.#formatter.read(reader);
      if (reader.remaining !== 0) throw new ChannelFramingError('Channel item does not decode to exactly its length.');
    } catch (error) {
      core.reset('item did not decode');
      if (error instanceof ChannelFramingError) throw new NexusError(error.message);
      throw new NexusError('Channel item does not decode to exactly its length.', { cause: error });
    }
    core.pendingItemLength = 0;
    core.consume(total);
    return { kind: 'item', value };
  }
}

/**
 * Writes typed items to a channel. Mirrors C# `INexusChannelWriter<T>`. Get one from
 * {@link NexusDuplexChannel#getWriter}.
 *
 * Items are serialized when the write is called, then written in call order, each with its length prefix. An item
 * larger than the peer's `maxMessageSize` rejects with `NexusMessageTooLargeError` and nothing is sent; the channel
 * stays usable. A write rejects when the channel's writing direction is complete or the pipe failed. An abort while a
 * write waits for credit rejects it; if part of the write was already sent, the writing direction is completed, as
 * the item stream can't be resumed.
 *
 * @template T
 */
export class NexusChannelWriter {
  /** @type {import('./PipeCore.js').PipeCore} */
  #core;
  /** @type {import('../contract/codec.js').Formatter<T>} */
  #formatter;
  /** Writes run one at a time. @type {Promise<unknown>} */
  #queue = Promise.resolve();

  /**
   * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
   * @param {import('../contract/codec.js').Formatter<T>} formatter
   */
  constructor(pipe, formatter) {
    this.#core = pipeCore(pipe);
    this.#formatter = formatter;
  }

  /**
   * @param {T} item
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  write(item, options) {
    return this.#write([item], options?.signal, false);
  }

  /**
   * Writes the items with one flush.
   * @param {Iterable<T>} items
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  writeMany(items, options) {
    return this.#write(items, options?.signal, false);
  }

  /** Completes the writing direction after the writes before it. @returns {Promise<void>} */
  complete() {
    return this.#serialize(() => this.#core.completeWriting());
  }

  /**
   * Writes the items, then completes.
   * @param {Iterable<T>} items
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  writeAndComplete(items, options) {
    return this.#write(items, options?.signal, true);
  }

  /**
   * @param {Iterable<T>} items
   * @param {AbortSignal | undefined} signal
   * @param {boolean} complete
   * @returns {Promise<void>}
   */
  #write(items, signal, complete) {
    /** @type {Uint8Array} */
    let bytes;
    try {
      if (signal?.aborted) throw cancellationError(signal);
      bytes = this.#frame(items);
    } catch (error) {
      return Promise.reject(error);
    }
    const core = this.#core;
    return this.#serialize(async () => {
      if (bytes.length > 0) await core.writeBytes(bytes, signal, true);
      if (complete) await core.completeWriting();
    });
  }

  /**
   * Serializes the items and frames each with its length prefix, all into one buffer.
   * @param {Iterable<T>} items
   * @returns {Uint8Array}
   */
  #frame(items) {
    const limit = this.#core.host.sendMaxMessageSize();
    /** @type {Uint8Array[]} */
    const serialized = [];
    let total = 0;
    for (const item of items) {
      const writer = invocationCodec().createWriter();
      this.#formatter.write(writer, item);
      const bytes = writer.toUint8Array().slice();
      if (bytes.length > limit) {
        throw new NexusMessageTooLargeError(`The channel item of ${bytes.length} bytes exceeds the negotiated maximum of ${limit} bytes.`);
      }
      serialized.push(bytes);
      total += prefixLength(bytes.length) + bytes.length;
    }
    const out = new Uint8Array(total);
    let offset = 0;
    for (const bytes of serialized) {
      offset += writeLength(out, offset, bytes.length);
      out.set(bytes, offset);
      offset += bytes.length;
    }
    return out;
  }

  /**
   * @template R
   * @param {() => Promise<R>} run
   * @returns {Promise<R>}
   */
  #serialize(run) {
    const result = this.#queue.then(run);
    this.#queue = result.catch(() => {});
    return result;
  }
}

/**
 * The channel forward's copy loop (C# `NexusPipeForwardingExtensions.CopyAsync` with a maximum item size): copies
 * the source's input to the destination's output, parsing each item's length prefix as it passes and checking it
 * against the largest item the destination's peer accepts. Item bodies are copied as they arrive.
 *
 * @param {import('./PipeCore.js').PipeCore} source
 * @param {import('./PipeCore.js').PipeCore} destination
 * @param {AbortSignal | undefined} signal Stops the forward; neither pipe is completed.
 * @returns {Promise<void>}
 */
async function forwardItems(source, destination, signal) {
  const max = destination.host.sendMaxMessageSize();
  // Bytes of the current item (its prefix included) still to pass through.
  let itemRemaining = 0;
  for (;;) {
    if (signal?.aborted) throw cancellationError(signal);
    if (destination.outputDone) return;

    if (source.buffered > 0 && itemRemaining === 0) {
      /** @type {{ length: number, size: number } | null} */
      let prefix;
      try {
        prefix = readLength(source.peek(MAX_PREFIX_LENGTH));
      } catch (error) {
        throw new NexusError(/** @type {Error} */ (error).message, { cause: error });
      }
      if (prefix === null) {
        if (source.inputDone) {
          // The source ended inside a prefix: pass the rest on as it is.
          itemRemaining = source.buffered;
        } else {
          // A split prefix: make sure the writer has the credit to send the rest of it, then wait for it.
          const version = source.inputVersion;
          source.ensureCredit(MAX_PREFIX_LENGTH);
          await source.waitInput(signal, version);
          continue;
        }
      } else {
        if (prefix.length > max) {
          throw new NexusMessageTooLargeError(`The channel item of ${prefix.length} bytes exceeds the negotiated maximum of ${max} bytes.`);
        }
        itemRemaining = prefix.size + prefix.length;
      }
    }

    if (source.buffered > 0) {
      if (destination.sendCredit <= 0) {
        await destination.waitCredit(signal);
        continue;
      }
      const chunk = source.peekChunk();
      const n = Math.min(chunk.length, itemRemaining, destination.sendCredit);
      // Sends without waiting (the credit is there); the bytes are copied into frames before it returns.
      const sent = destination.writeBytes(chunk.subarray(0, n));
      source.consume(n);
      itemRemaining -= n;
      try {
        await sent;
      } catch (error) {
        if (destination.outputDone && destination.failure === null) return;
        throw error;
      }
      continue;
    }

    if (source.inputDone) {
      await destination.completeWriting();
      return;
    }
    await source.waitInput(signal);
  }
}

/**
 * A typed channel over a duplex pipe. Mirrors C# `INexusDuplexChannel<T>`. Created by
 * `client.createChannel(formatter)` (passed to a channel method of the server proxy), by the runtime for a channel
 * parameter of a client nexus method, or over any pipe with `new NexusDuplexChannel(pipe, formatter)`.
 *
 * @template T
 */
export class NexusDuplexChannel {
  /** @type {import('./NexusDuplexPipe.js').NexusDuplexPipe} */
  #pipe;
  /** @type {import('../contract/codec.js').Formatter<T>} */
  #formatter;
  /** @type {NexusChannelReader<T> | null} */
  #reader = null;
  /** @type {NexusChannelWriter<T> | null} */
  #writer = null;

  /**
   * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
   * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
   */
  constructor(pipe, formatter) {
    pipeCore(pipe);
    if (!formatter || typeof formatter.read !== 'function' || typeof formatter.write !== 'function') {
      throw new TypeError('A channel needs an item formatter ({ read, write }).');
    }
    this.#pipe = pipe;
    this.#formatter = formatter;
  }

  /** The underlying pipe. @returns {import('./NexusDuplexPipe.js').NexusDuplexPipe} */
  get pipe() {
    return this.#pipe;
  }

  /** The channel's reader, once the pipe is ready. The same reader every time. @returns {Promise<NexusChannelReader<T>>} */
  async getReader() {
    await this.#pipe.ready;
    this.#reader ??= new NexusChannelReader(this.#pipe, this.#formatter);
    return this.#reader;
  }

  /** The channel's writer, once the pipe is ready. The same writer every time. @returns {Promise<NexusChannelWriter<T>>} */
  async getWriter() {
    await this.#pipe.ready;
    this.#writer ??= new NexusChannelWriter(this.#pipe, this.#formatter);
    return this.#writer;
  }

  /** Completes both directions of the pipe. @returns {Promise<void>} */
  complete() {
    return this.#pipe.complete();
  }

  /**
   * Forwards the items this channel receives to `destination` as raw bytes, without decoding or encoding them, until
   * this channel's input completes; then half-closes `destination` (completes its writing direction). Mirrors C#
   * `ForwardToAsync` for channels.
   *
   * Items stream through as they arrive, so an item larger than the pipe window is not buffered whole. The forward
   * reads one buffer at a time and sends only what the destination's credit allows before consuming it, so a slow
   * destination stalls this channel's writer instead of buffering. Each item's length prefix is checked against what
   * the destination's peer accepts as it passes. At the end of the input an incomplete item is passed on as it is.
   * This channel is never completed. Do not read this channel or write `destination` while the forward runs.
   *
   * @param {NexusDuplexChannel<T>} destination
   * @param {{ signal?: AbortSignal }} [options] Stops the forward; neither channel is completed. Items already
   *   forwarded stay delivered, and this channel's input is positioned after them.
   * @returns {Promise<void>} Resolves when this channel's input completed or `destination`'s output was completed.
   *   Rejects with `NexusMessageTooLargeError` when an item is larger than the destination's peer accepts: none of
   *   it was sent, the items before it were, neither channel is completed and the item stays first in this channel's
   *   input. Rejects with a `NexusError` on a malformed item prefix, with the destination's error when its pipe
   *   failed, and with the abort reason when aborted.
   */
  async forwardTo(destination, options) {
    if (!(destination instanceof NexusDuplexChannel)) throw new TypeError('The destination must be a NexusDuplexChannel.');
    if (destination.pipe === this.#pipe) throw new NexusError('A channel can not be forwarded to itself.');
    const signal = options?.signal;
    if (signal?.aborted) throw cancellationError(signal);
    const source = pipeCore(this.#pipe);
    const target = pipeCore(destination.pipe);
    await Promise.all([source.ready, target.ready]);
    await forwardItems(source, target, signal);
  }

  /** Releases the pipe. @returns {Promise<void>} */
  [Symbol.asyncDispose]() {
    return this.#pipe[Symbol.asyncDispose]();
  }
}
