/**
 * Incremental frame parser (protocol spec §1, §2). Bytes are appended to a growable buffer and every complete frame
 * is handed to the sink synchronously, in the data callback that completed it.
 * @module
 */
export type Frame = {
    /**
     * Message type byte.
     */
    type: number;
    /**
     * Empty for `Ping`.
     */
    body: Uint8Array;
    /**
     * `DuplexPipeWrite`/`PipeWindowUpdate`: `clientId | serverId << 8`; otherwise 0.
     */
    pipeId: number;
    /**
     * `MessageChunk`: the invocation ID; otherwise 0.
     */
    invocationId: number;
    /**
     * `MessageChunk`: the large-message kind; otherwise 0.
     */
    kind: number;
};
export type PushResult = {
    kind: 'more';
} | {
    kind: 'stopped';
} | {
    kind: 'disconnect';
    reason: number;
} | {
    kind: 'error';
    reason: number;
    message: string;
};
export declare class FrameReader {
    #private;
    /** True once the connection completed: lifts the 16 KiB pre-auth body cap (§2). */
    handshakeComplete: boolean;
    /** Bytes buffered and not yet consumed (an incomplete frame). @returns {number} */
    get buffered(): number;
    /**
     * Appends `chunk` and parses every complete frame.
     * @param {Uint8Array} chunk
     * @param {(frame: Frame) => boolean} sink Return false to stop parsing (the session is ending).
     * @returns {PushResult}
     */
    push(chunk: Uint8Array, sink: (frame: Frame) => boolean): PushResult;
}
