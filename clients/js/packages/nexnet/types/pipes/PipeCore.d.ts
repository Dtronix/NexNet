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
export type PipeResetReason = 'invalid_item' | 'too_large';
export type PipeHost = {
    /**
     * Queues a frame after every frame queued before it.
     */
    send: (frame: Uint8Array) => Promise<boolean>;
    /**
     * Asks the control sender to drain the pipe's window grant.
     */
    requestControl: (core: PipeCore) => void;
    /**
     * Called after a state change, so a fully closed pipe is freed.
     */
    changed: (core: PipeCore) => void;
    /**
     * Session clock.
     */
    now: () => number;
    budget: import('../invocation/SessionBudget.js').SessionBudget;
    /**
     * This side's receive window per pipe (`pipeWindow`).
     */
    window: number;
    /**
     * Largest channel item the peer accepts.
     */
    sendMaxMessageSize: () => number;
    /**
     * Largest channel item this side accepts.
     */
    maxMessageSize: number;
    serializerOptions: import('../contract/codec.js').SerializerOptions;
    log: (level: import('../contract/config.js').NexusLogLevel, message: string, error?: unknown) => void;
    /**
     * Reports an event
     * instrument (`session.recordMetric`).
     */
    metric: (name: string, value: number, tags?: Readonly<Record<string, string>>) => void;
};
export type PipePhase = 'created' | 'invoked' | 'open' | 'failed';
export declare class PipeCore {
    #private;
    /** The manager that owns the pipe. @type {import('./PipeManager.js').PipeManager} */
    manager: import('./PipeManager.js').PipeManager;
    /** @type {PipeHost} */
    host: PipeHost;
    /** This side's local ID (1–255). */
    localId: number;
    /** The full ID (`clientId | serverId << 8`), or 0 until the peer's Ready arrives on the opening side. */
    id: number;
    /** True for a pipe this side opened (`createPipe`), false for one it registered. */
    initiating: boolean;
    /** @type {PipePhase} */
    phase: PipePhase;
    /** State flags (§4.6). @type {number} */
    state: number;
    /** Why the pipe can't be used: refused, the invocation failed, or the session ended. @type {unknown} */
    failure: unknown;
    /** @type {Promise<void>} */
    ready: Promise<void>;
    /** @type {Promise<void>} */
    completed: Promise<void>;
    /** Bytes buffered and not consumed. */
    buffered: number;
    /** True once no more data will be delivered: the peer's writer or this side's reader completed. */
    inputDone: boolean;
    /** Incremented whenever data arrives or the input completes; lets waiters skip data that arrived meanwhile. */
    inputVersion: number;
    /** Bytes the peer may still send. */
    recvCredit: number;
    /** Bytes consumed since credit was last granted back. */
    consumedSinceGrant: number;
    /** Extra credit granted for an item larger than the window (§7.4), backed by a budget reservation. */
    extraOutstanding: number;
    /** Clock time of the last data or extra grant. */
    lastDataAt: number;
    /** True once data arrived or extra credit was granted, so {@link PipeCore#lastDataAt} is meaningful. */
    dataSeen: boolean;
    /** Length (prefix included) of a channel item that has partly arrived; 0 if none. */
    pendingItemLength: number;
    /** Bytes this side may still send. */
    sendCredit: number;
    /** True once this side may not send any more: its writer completed, or the peer's reader did. */
    outputDone: boolean;
    /** True once this side completed its own writer (so the writable is not errored for it). */
    localWriterDone: boolean;
    /** Called once when the output completes for a reason other than this side's writer. @type {(() => void) | null} */
    onOutputDone: (() => void) | null;
    sentClose: boolean;
    receivedClose: boolean;
    released: boolean;
    freed: boolean;
    closingSince: number;
    /**
     * @param {import('./PipeManager.js').PipeManager} manager
     * @param {PipeHost} host
     * @param {number} localId
     * @param {number} fullId 0 for a pipe this side opens.
     */
    constructor(manager: import('./PipeManager.js').PipeManager, host: PipeHost, localId: number, fullId: number);
    /** True once this side sent its Complete, received the peer's, and the application released the pipe. */
    get isFullyClosed(): boolean;
    /**
     * Adds state flags (C# `UpdateState`). Completing a direction completes this side's reader or writer.
     * @param {number} flags
     * @returns {boolean} True when the state changed.
     */
    updateState(flags: number): boolean;
    /**
     * Sends the current state (C# `NotifyState`). Sending `Complete` is this side's close: sent once, after which
     * the pipe is closing until the peer's arrives.
     * @returns {Promise<void>}
     */
    notifyState(): Promise<void>;
    /** Registering side: opens the receive window and sends Ready with it. @returns {Promise<void>} */
    sendReady(): Promise<void>;
    /**
     * Handles the peer's `DuplexPipeUpdateState` (C# `OnRemoteState`).
     * @param {number} state
     * @param {number} window
     * @returns {number} A disconnect reason, or 0.
     */
    onRemoteState(state: number, window: number): number;
    /** The peer never opened the pipe, so there is no close to exchange. */
    markClosedWithoutExchange(): void;
    /**
     * The pipe method was not sent (canceled before send, not connected, too large): the pipe fails with `error`.
     * @param {unknown} error
     */
    invokeFailed(error: unknown): void;
    /**
     * The session ended (C# `CancelAll`). An open pipe completes both directions (buffered data stays readable); a
     * pipe that never opened fails as canceled, with the disconnect reason as the cause (amendment C-2).
     * @param {number} reason
     */
    sessionEnded(reason: number): void;
    /**
     * Sends this side's Complete if it has not been sent (C# `CloseAsync`).
     * @returns {Promise<void>}
     */
    close(): Promise<void>;
    /**
     * Completes both directions (`complete()`, C# `CompleteAsync`). Before the pipe is ready the completion is sent
     * once it is.
     * @returns {Promise<void>}
     */
    complete(): Promise<void>;
    /**
     * This side's writer is done (half-close of the client-to-server direction).
     * @returns {Promise<void>}
     */
    completeWriting(): Promise<void>;
    /**
     * This side's reader is done: buffered data is dropped and data still arriving is discarded.
     * @returns {Promise<void>}
     */
    completeReading(): Promise<void>;
    /**
     * Resets the pipe after a bad channel item (C# `Reset`): drops the input and closes the pipe. The session stays up.
     * @param {string} why
     * @param {PipeResetReason} [reason]
     * @returns {void}
     */
    reset(why: string, reason?: PipeResetReason): void;
    /**
     * The application is done with the pipe (dispose): fails a pending `ready`, completes the directions and drops
     * buffered data. The manager sends Complete and frees the pipe.
     */
    dispose(): void;
    /**
     * Buffers data from the peer (C# `BufferData`). Never waits.
     * @param {Uint8Array} body The frame body; copied.
     * @returns {boolean} False when the data breaks the protocol: beyond the credit, or after the peer's Complete.
     */
    onData(body: Uint8Array): boolean;
    /**
     * Waits until data arrives or the input completes after `version` (an earlier {@link PipeCore#inputVersion}).
     * Resolves at once when that already happened.
     * @param {AbortSignal | undefined} signal
     * @param {number} [version]
     * @returns {Promise<void>}
     */
    waitInput(signal: AbortSignal | undefined, version?: number): Promise<void>;
    /**
     * The first `n` buffered bytes (fewer if fewer are buffered), without consuming them. A view when they are in
     * one chunk, otherwise a copy.
     * @param {number} n
     * @returns {Uint8Array}
     */
    peek(n: number): Uint8Array;
    /**
     * The rest of the first buffered chunk, without consuming it.
     * @returns {Uint8Array}
     */
    peekChunk(): Uint8Array;
    /**
     * Removes and returns the rest of the first buffered chunk. The chunk's buffer is not used again by the pipe, so
     * it may be transferred (a byte stream's `enqueue`).
     * @returns {Uint8Array}
     */
    takeChunk(): Uint8Array;
    /**
     * Consumes `n` buffered bytes; credit is granted back as they are (§7.2).
     * @param {number} n
     */
    consume(n: number): void;
    /** Drops everything buffered (the reader completed or the pipe was disposed). Grants nothing back. */
    dropInput(): void;
    /**
     * Makes sure the writer can send `missing` more bytes: if the credit it still holds is less, grants everything
     * consumed but not yet granted back, without waiting for half the window (§7.2, flow-control.md §12).
     * @param {number} missing
     */
    ensureCredit(missing: number): void;
    /**
     * Makes room for an item larger than the window (§7.4): reserves `extra` bytes from the session budget, all at
     * once (waiting in FIFO order until it fits), and grants them as extra credit. Does nothing while an earlier extra
     * grant is still outstanding. Resolves false when the wait was canceled or the session ended.
     * @param {number} extra
     * @param {AbortSignal} [signal]
     * @returns {Promise<boolean>}
     */
    grantExtra(extra: number, signal?: AbortSignal): Promise<boolean>;
    /**
     * Queues a window grant; grants are coalesced until the control sender drains them.
     * @param {number} bytes
     */
    grant(bytes: number): void;
    /**
     * The pending window grant as frames (`PipeControlSource`). Called by the control sender at flush time.
     * @returns {Uint8Array[]}
     */
    takePendingControl(): Uint8Array[];
    /**
     * Describes how this pipe stalled for the log, or null: closing without the peer's confirmation, or an item given
     * extra credit whose data stopped arriving (§9).
     * @param {number} now
     * @param {number} timeout
     * @returns {string | null}
     */
    describeStall(now: number, timeout: number): string | null;
    /**
     * Adds send credit granted by the peer.
     * @param {number} bytes
     * @returns {boolean} False when the total would exceed 2^31 (a `FlowControl` violation).
     */
    addSendCredit(bytes: number): boolean;
    /**
     * Waits until send credit is available or the output completes. Waiting for credit is never a stall (§9). A wait
     * that parks is shown in snapshots (`waitingForCredit`, `waitingBytes`) and recorded to
     * `nexnet.pipe.credit_wait` when it ends.
     * @param {AbortSignal | undefined} signal
     * @param {number} [waitingBytes] Bytes the writer still has to send.
     * @returns {Promise<void>}
     */
    waitCredit(signal: AbortSignal | undefined, waitingBytes?: number): Promise<void>;
    /**
     * A snapshot of the pipe (C# `NexusDuplexPipe.GetSnapshot`). Durations are milliseconds on the session clock.
     * @param {number} now
     * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot}
     */
    snapshot(now: number): import('../diagnostics/snapshot.js').NexusPipeSnapshot;
    /** The error of a write that can't be sent. @returns {unknown} */
    outputClosedError(): unknown;
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
    writeBytes(bytes: Uint8Array, signal?: AbortSignal, framed?: boolean): Promise<void>;
}
