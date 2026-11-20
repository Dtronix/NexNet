/**
 * The invocation layer of one session (protocol spec §4.3–§4.10, §6, §8): outbound calls with invocation IDs,
 * credits, cancellation and results; server-to-client calls with method validation, two execution pools, per-call
 * cancellation and results; large arguments and results in both directions.
 *
 * The client attaches one manager to every session before the session starts ({@link attachInvocations}), so frames
 * that arrive in the same read as the ServerGreeting find their handlers. Frames are handled synchronously in the
 * transport's data callback; user code (nexus methods) and promise settlement never run re-entrantly from there.
 * @module
 */
import { NexusRemoteInvocationError } from '../contract/errors.js';
import { SessionBudget } from './SessionBudget.js';
/** Largest serialized arguments that are never streamed (§6.1): 65,535 minus the 9-byte worst-case body overhead. */
export declare const MAX_ARGUMENT_SIZE: number;
export type OutgoingLargeMessage = {
    id: number;
    /**
     * A {@link LargeMessageKind}.
     */
    kind: number;
    length: number;
    /**
     * Bytes of the body queued so far.
     */
    sent: number;
    /**
     * Clock time of the last chunk.
     */
    lastProgress: number;
    aborted: boolean;
    reason: number;
};
export type NexusHooks = {
    _dispatch: (methodId: number, reader: import('../contract/codec.js').MsgPackReader, ctx: import('../contract/nexus.js').DispatchContext) => void | Promise<void>;
    onInvocationException?: (error: unknown, methodId: number) => ({
        code?: number | null;
        message?: string | null;
    } | null | undefined);
    _separatePool?: (methodId: number) => boolean;
};
export type PipeHooks = {
    pipe: (pipeId: number, ctx: import('../contract/nexus.js').DispatchContext) => import('../contract/pipes.js').NexusDuplexPipe;
    channel: (pipeId: number, formatter: import('../contract/codec.js').Formatter<any>, ctx: import('../contract/nexus.js').DispatchContext) => import('../contract/pipes.js').NexusDuplexChannel<any>;
    /**
     * Called when a dispatched
     * invocation returns or throws: the registering side closes the pipes it registered for it (§7.6).
     */
    release?: (ctx: import('../contract/nexus.js').DispatchContext) => void;
};
export type OutboundCall = {
    /**
     * False for IgnoreReturn invocations.
     */
    expectResult: boolean;
    signal: AbortSignal | undefined;
    phase: 'waiting' | 'sending' | 'sent';
    /**
     * The reserved invocation ID, or -1.
     */
    id: number;
    settled: boolean;
    resolve: (value: Uint8Array | null) => void;
    reject: (error: unknown) => void;
    onAbort: (() => void) | null;
};
export type QueuedInvocation = {
    message: import('./messages.js').InvocationMessage;
    controller: AbortController;
    /**
     * Budget bytes held until the invocation finishes (a reassembled large argument).
     */
    reserved: number;
    queuedAt: number;
};
export type InvocationPool = {
    separate: boolean;
    max: number;
    free: number;
    queue: QueuedInvocation[];
};
/**
 * Attaches an invocation manager to a session that has not started yet.
 * @param {import('../session/NexusSession.js').NexusSession} session
 * @param {object} init
 * @param {NexusHooks | object} init.nexus The client nexus.
 * @param {import('../contract/handshake.js').HandshakeDescriptor} init.handshake
 * @returns {InvocationManager}
 */
export declare function attachInvocations(session: import('../session/NexusSession.js').NexusSession, { nexus, handshake }: {
    nexus: NexusHooks | object;
    handshake: import('../contract/handshake.js').HandshakeDescriptor;
}): InvocationManager;
/**
 * The manager of a session, or null.
 * @param {import('../session/NexusSession.js').NexusSession | null | undefined} session
 * @returns {InvocationManager | null}
 */
export declare function getInvocationManager(session: import('../session/NexusSession.js').NexusSession | null | undefined): InvocationManager | null;
/**
 * Frees the execution slot of a dispatched invocation before it completes (a pipe or channel method whose pipe is
 * open, §8). Idempotent.
 * @param {import('../contract/nexus.js').DispatchContext} ctx
 * @returns {void}
 */
export declare function releaseInvocationSlot(ctx: import('../contract/nexus.js').DispatchContext): void;
export declare class InvocationManager {
    #private;
    /** The session's receive budget; the pipe layer (J08) reserves its windows from it. @type {SessionBudget} */
    budget: SessionBudget;
    /** Installed by the pipe layer (J08). @type {PipeHooks | null} */
    pipeHooks: PipeHooks | null;
    /**
     * @param {import('../session/NexusSession.js').NexusSession} session
     * @param {NexusHooks} nexus
     * @param {import('../contract/handshake.js').HandshakeDescriptor} handshake
     */
    constructor(session: import('../session/NexusSession.js').NexusSession, nexus: NexusHooks, handshake: import('../contract/handshake.js').HandshakeDescriptor);
    /** True while calls can be sent. @returns {boolean} */
    get isConnected(): boolean;
    /** Reader options for results and arguments. @returns {import('../contract/codec.js').SerializerOptions} */
    get serializerOptions(): import('../contract/codec.js').SerializerOptions;
    /** Outbound calls not yet completed. @returns {number} */
    get pendingCount(): number;
    /** Outbound invocation credits available now. @returns {number} */
    get availableCredits(): number;
    /** Server-to-client invocations queued or running. @returns {number} */
    get inboundCount(): number;
    /** Calls waiting for the peer to return invocation credit. @returns {number} */
    get creditWaits(): number;
    /**
     * Server-to-client invocations queued and running, per execution pool (the `nexnet.invocation.queued` and
     * `nexnet.invocation.executing` instruments).
     * @returns {{ queued: number, executing: number, separatePoolQueued: number, separatePoolExecuting: number }}
     */
    get poolCounts(): {
        queued: number;
        executing: number;
        separatePoolQueued: number;
        separatePoolExecuting: number;
    };
    /**
     * Large messages being received and sent (the `nexnet.large_message.in_flight` instrument).
     * @returns {{ inbound: number, outbound: number }}
     */
    get largeMessageCounts(): {
        inbound: number;
        outbound: number;
    };
    /**
     * The invocation part of the snapshot (C# `NexusInvocationSnapshot`). Waits are milliseconds on the session clock.
     * A client never drops invocations for lack of credit (only a server's multi-session invocations do), so
     * `creditDrops` is always 0.
     * @param {number} [now]
     * @returns {import('../diagnostics/snapshot.js').NexusInvocationSnapshot}
     */
    snapshot(now?: number): import('../diagnostics/snapshot.js').NexusInvocationSnapshot;
    /**
     * Snapshots of the large messages being received and sent (C# `NexusLargeMessageSnapshot`).
     * @param {number} [now]
     * @returns {import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]}
     */
    largeMessageSnapshots(now?: number): import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[];
    /**
     * Sends an invocation. With `expectResult` it resolves with the result value (the embedded MessagePack, or null when
     * the result carries none); without, it is an IgnoreReturn invocation that resolves once sent.
     *
     * Waits in FIFO order for invocation credit. A cancel before send rejects with the cancellation error and sends
     * nothing; a cancel after send (result calls only) rejects at once, sends `InvocationCancellation` and consumes the
     * late result (§4.5).
     *
     * @param {number} methodId
     * @param {Uint8Array | null} args The serialized arguments array; null for none.
     * @param {{ signal?: AbortSignal, flags?: number, expectResult: boolean }} options
     * @returns {Promise<Uint8Array | null>}
     */
    invoke(methodId: number, args: Uint8Array | null, { signal, flags, expectResult }: {
        signal?: AbortSignal;
        flags?: number;
        expectResult: boolean;
    }): Promise<Uint8Array | null>;
}
/**
 * An `AbortError` whose `cause` is a `NexusTransportError` with the disconnect reason (amendment C-2). `cause` is
 * set as an own property: not every platform's `DOMException` constructor accepts it.
 * @param {number} reason
 * @returns {DOMException}
 */
export declare function sessionEndedError(reason: number): DOMException;
/**
 * The error of an Exception result. The message mirrors C# `ProxyRemoteInvocationException`.
 * @param {import('./messages.js').RemoteErrorDetail | null} detail
 * @returns {NexusRemoteInvocationError}
 */
export declare function remoteInvocationError(detail: import('./messages.js').RemoteErrorDetail | null): NexusRemoteInvocationError;
