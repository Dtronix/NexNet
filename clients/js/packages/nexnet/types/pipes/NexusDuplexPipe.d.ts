/**
 * The public duplex pipe (C# `INexusDuplexPipe`) over a {@link PipeCore}.
 * @module
 */
/**
 * The state behind a pipe the runtime created. Internal to the runtime.
 * @param {NexusDuplexPipe} pipe
 * @returns {import('./PipeCore.js').PipeCore}
 */
export declare function pipeCore(pipe: NexusDuplexPipe): import('./PipeCore.js').PipeCore;
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
export declare class NexusDuplexPipe {
    #private;
    /**
     * Created by the runtime.
     * @param {import('./PipeCore.js').PipeCore} core
     */
    constructor(core: import('./PipeCore.js').PipeCore);
    /** Local pipe ID (1–255), or 0 while unassigned. @returns {number} */
    get id(): number;
    /**
     * Incoming bytes: a byte stream (`type: 'bytes'`, BYOB readers supported) with `highWaterMark` 0, so nothing is
     * pulled, and no credit granted, before a read. Cancelling it completes this side's reading direction.
     * @returns {ReadableStream<Uint8Array>}
     */
    get readable(): ReadableStream<Uint8Array>;
    /**
     * Outgoing bytes. A write resolves once its bytes were handed to the transport, waiting for credit as needed.
     * `highWaterMark` is one chunk: with 0, `ReadableStream.pipeTo` would never write (it waits for a positive
     * `desiredSize`); the sink still takes one chunk at a time and sends only what the credit allows. Closing or
     * aborting it half-closes the pipe. It errors when the server completes its reading direction or the pipe fails.
     * @returns {WritableStream<Uint8Array>}
     */
    get writable(): WritableStream<Uint8Array>;
    /**
     * Resolves when both sides are ready. Rejects with `NexusBusyError` when the server refused the pipe (a limit or
     * authorization), with the invocation's error when the pipe method could not be sent, and with an `AbortError`
     * when the pipe was disposed or the session ended first.
     * @returns {Promise<void>}
     */
    get ready(): Promise<void>;
    /** Resolves when both directions are complete. @returns {Promise<void>} */
    get completed(): Promise<void>;
    /** Completes both directions. @returns {Promise<void>} */
    complete(): Promise<void>;
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
    forwardTo(destination: NexusDuplexPipe, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Forwards this pipe to `other` and `other` to this pipe; see {@link NexusDuplexPipe.bridge}.
     * @param {NexusDuplexPipe} other
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<void>}
     */
    bridge(other: NexusDuplexPipe, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
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
    static bridge(a: NexusDuplexPipe, b: NexusDuplexPipe, options?: {
        signal?: AbortSignal;
    }): Promise<void>;
    /**
     * Releases the pipe: sends the close if needed, fails a pending `ready`, and frees the pipe ID and receive window
     * once the server closed too.
     * @returns {Promise<void>}
     */
    [Symbol.asyncDispose](): Promise<void>;
}
