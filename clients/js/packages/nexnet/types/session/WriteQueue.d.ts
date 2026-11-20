/**
 * The session's write path: frames are queued and flushed once per microtask, in one `transport.write` call. While a
 * write is in flight (transport backpressure), new frames accumulate and go out in the next write once it resolves.
 * @module
 */
export declare class WriteQueue {
    #private;
    /** Clock time the write in flight started; meaningful only while {@link WriteQueue#inFlight}. Used by the progress timeout (§9). */
    flushStartedAt: number;
    /** Clock time the last transport write completed; null until one has. @type {number | null} */
    lastSentAt: number | null;
    /** Frames and bytes handed to the transport. */
    framesSent: number;
    bytesSent: number;
    /**
     * @param {import('../transport/ITransport.js').Transport} transport
     * @param {object} options
     * @param {() => Uint8Array[]} options.beforeFlush Returns control frames to send with the queued frames.
     * @param {() => boolean} options.hasControl True when `beforeFlush` has frames to return.
     * @param {(error: unknown) => void} options.onError Called once when a transport write fails.
     * @param {() => number} options.now
     */
    constructor(transport: import('../transport/ITransport.js').Transport, { beforeFlush, hasControl, onError, now }: {
        beforeFlush: () => Uint8Array[];
        hasControl: () => boolean;
        onError: (error: unknown) => void;
        now: () => number;
    });
    /** Frames queued and not yet handed to the transport. @returns {number} */
    get pendingCount(): number;
    /** True while a transport write is in flight. @returns {boolean} */
    get inFlight(): boolean;
    /**
     * Queues a frame. Resolves true once the transport accepted the write that carried it, false when the queue
     * closed first (the session ended) or the write failed.
     * @param {Uint8Array} frame
     * @returns {Promise<boolean>}
     */
    enqueue(frame: Uint8Array): Promise<boolean>;
    /** Requests a flush (for control frames, which are pulled through `beforeFlush`). @returns {void} */
    schedule(): void;
    /**
     * Queues `frame` as the last frame (a disconnect byte), refuses later ones, and resolves once it is written (true),
     * or false when the write failed or the queue closed. The frames already queued are written before it, behind the
     * write in flight (specification §5.6).
     * @param {Uint8Array} frame
     * @returns {Promise<boolean>}
     */
    finish(frame: Uint8Array): Promise<boolean>;
    /** Drops queued frames (their promises resolve false) and refuses new ones. @returns {void} */
    close(): void;
}
