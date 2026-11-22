/**
 * Typed channels over a duplex pipe (protocol spec §7.4, C# `NexusDuplexChannel`, `NexusChannelReader`,
 * `NexusChannelWriter`): each item is one MessagePack value preceded by its length as a varint.
 * @module
 */
export type Decoded<T> = {
    kind: 'item';
    value: T;
} | {
    kind: 'none';
} | {
    kind: 'partial';
    length: number;
    missing: number;
};
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
export declare class NexusChannelReader<T> {
    #private;
    /**
     * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
     * @param {import('../contract/codec.js').Formatter<T>} formatter
     */
    constructor(pipe: import('./NexusDuplexPipe.js').NexusDuplexPipe, formatter: import('../contract/codec.js').Formatter<T>);
    /** True once the writer completed and every item was read. @returns {boolean} */
    get isComplete(): boolean;
    /**
     * Reads one item.
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<IteratorResult<T, undefined>>}
     */
    read(options?: {
        signal?: AbortSignal;
    }): Promise<IteratorResult<T, undefined>>;
    /**
     * Reads up to `max` items that are available, waiting for at least one. Empty once complete.
     * @param {number} max
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<T[]>}
     */
    readBatch(max: number, options?: {
        signal?: AbortSignal;
    }): Promise<T[]>;
    /**
     * Reads every remaining item.
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<T[]>}
     */
    readUntilComplete(options?: {
        signal?: AbortSignal;
    }): Promise<T[]>;
    /** @returns {AsyncGenerator<T, void, undefined>} */
    [Symbol.asyncIterator](): AsyncGenerator<T, void, undefined>;
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
export declare class NexusChannelWriter<T> {
    #private;
    /**
     * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
     * @param {import('../contract/codec.js').Formatter<T>} formatter
     */
    constructor(pipe: import('./NexusDuplexPipe.js').NexusDuplexPipe, formatter: import('../contract/codec.js').Formatter<T>);
    /**
     * @param {T} item
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    write(item: T, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Writes the items with one flush.
     * @param {Iterable<T>} items
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    writeMany(items: Iterable<T>, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /** Completes the writing direction after the writes before it. @returns {Promise<void>} */
    complete(): Promise<void>;
    /**
     * Writes the items, then completes.
     * @param {Iterable<T>} items
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    writeAndComplete(items: Iterable<T>, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
}
/**
 * A typed channel over a duplex pipe. Mirrors C# `INexusDuplexChannel<T>`. Created by
 * `client.createChannel(formatter)` (passed to a channel method of the server proxy), by the runtime for a channel
 * parameter of a client nexus method, or over any pipe with `new NexusDuplexChannel(pipe, formatter)`.
 *
 * @template T
 */
export declare class NexusDuplexChannel<T> {
    #private;
    /**
     * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
     * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
     */
    constructor(pipe: import('./NexusDuplexPipe.js').NexusDuplexPipe, formatter: import('../contract/codec.js').Formatter<T>);
    /** The underlying pipe. @returns {import('./NexusDuplexPipe.js').NexusDuplexPipe} */
    get pipe(): import('./NexusDuplexPipe.js').NexusDuplexPipe;
    /** The channel's reader, once the pipe is ready. The same reader every time. @returns {Promise<NexusChannelReader<T>>} */
    getReader(): Promise<NexusChannelReader<T>>;
    /** The channel's writer, once the pipe is ready. The same writer every time. @returns {Promise<NexusChannelWriter<T>>} */
    getWriter(): Promise<NexusChannelWriter<T>>;
    /** Completes both directions of the pipe. @returns {Promise<void>} */
    complete(): Promise<void>;
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
    forwardTo(destination: NexusDuplexChannel<T>, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /** Releases the pipe. @returns {Promise<void>} */
    [Symbol.asyncDispose](): Promise<void>;
}
