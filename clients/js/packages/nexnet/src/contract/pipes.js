/**
 * Duplex pipes and channels. J08 implements them in `src/pipes/`.
 * @module
 */

import { notImplemented } from './internal.js';

/**
 * A duplex byte pipe. Mirrors C# `INexusDuplexPipe`. Credit is granted to the peer only as `readable` is
 * consumed.
 */
export class NexusDuplexPipe {
  /** Local pipe ID (1–255), or 0 while unassigned. @returns {number} */
  get id() { throw notImplemented('NexusDuplexPipe.id'); }

  /** Incoming bytes (`highWaterMark` 0). @returns {ReadableStream<Uint8Array>} */
  get readable() { throw notImplemented('NexusDuplexPipe.readable'); }

  /** Outgoing bytes (`highWaterMark` 0); closing it half-closes the pipe. @returns {WritableStream<Uint8Array>} */
  get writable() { throw notImplemented('NexusDuplexPipe.writable'); }

  /** Resolves when both sides are ready. @returns {Promise<void>} */
  get ready() { throw notImplemented('NexusDuplexPipe.ready'); }

  /** Resolves when both directions are complete. @returns {Promise<void>} */
  get completed() { throw notImplemented('NexusDuplexPipe.completed'); }

  /** Completes both directions. @returns {Promise<void>} */
  complete() { throw notImplemented('NexusDuplexPipe.complete'); }

  /** @returns {Promise<void>} */
  [Symbol.asyncDispose]() { throw notImplemented('NexusDuplexPipe[Symbol.asyncDispose]'); }
}

/**
 * Reads typed items from a channel. Mirrors C# `INexusChannelReader<T>`.
 *
 * @template T
 */
export class NexusChannelReader {
  /** True once the writer completed and every item was read. @returns {boolean} */
  get isComplete() { throw notImplemented('NexusChannelReader.isComplete'); }

  /**
   * Reads one item.
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<IteratorResult<T, undefined>>}
   */
  read(options) { throw notImplemented('NexusChannelReader.read'); }

  /**
   * Reads up to `max` items that are available, waiting for at least one. Empty once complete.
   * @param {number} max
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<T[]>}
   */
  readBatch(max, options) { throw notImplemented('NexusChannelReader.readBatch'); }

  /**
   * Reads every remaining item.
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<T[]>}
   */
  readUntilComplete(options) { throw notImplemented('NexusChannelReader.readUntilComplete'); }

  /** @returns {AsyncIterator<T>} */
  [Symbol.asyncIterator]() { throw notImplemented('NexusChannelReader[Symbol.asyncIterator]'); }
}

/**
 * Writes typed items to a channel. Mirrors C# `INexusChannelWriter<T>`.
 *
 * @template T
 */
export class NexusChannelWriter {
  /**
   * @param {T} item
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  write(item, options) { throw notImplemented('NexusChannelWriter.write'); }

  /**
   * Writes the items with one flush.
   * @param {Iterable<T>} items
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  writeMany(items, options) { throw notImplemented('NexusChannelWriter.writeMany'); }

  /** @returns {Promise<void>} */
  complete() { throw notImplemented('NexusChannelWriter.complete'); }

  /**
   * Writes the items, then completes.
   * @param {Iterable<T>} items
   * @param {{ signal?: AbortSignal }} [options]
   * @returns {Promise<void>}
   */
  writeAndComplete(items, options) { throw notImplemented('NexusChannelWriter.writeAndComplete'); }
}

/**
 * A typed channel over a duplex pipe. Mirrors C# `INexusDuplexChannel<T>`.
 *
 * @template T
 */
export class NexusDuplexChannel {
  /** The underlying pipe. @returns {NexusDuplexPipe} */
  get pipe() { throw notImplemented('NexusDuplexChannel.pipe'); }

  /** @returns {Promise<NexusChannelReader<T>>} */
  getReader() { throw notImplemented('NexusDuplexChannel.getReader'); }

  /** @returns {Promise<NexusChannelWriter<T>>} */
  getWriter() { throw notImplemented('NexusDuplexChannel.getWriter'); }

  /** @returns {Promise<void>} */
  complete() { throw notImplemented('NexusDuplexChannel.complete'); }

  /** @returns {Promise<void>} */
  [Symbol.asyncDispose]() { throw notImplemented('NexusDuplexChannel[Symbol.asyncDispose]'); }
}
