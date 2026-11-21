/**
 * One NexNet session over a connected transport: framing, handshake, keepalive, timeouts and disconnect (protocol
 * spec §1, §2, §5, §9). Invocations, pipes and collections plug in through {@link NexusSession#setHandler},
 * {@link NexusSession#send}, {@link NexusSession#controlSender}, {@link NexusSession#onClose} and
 * {@link NexusSession#addProgressProbe}.
 *
 * Every complete frame is handled synchronously in the transport's data callback. The session never calls user code
 * synchronously from there: the client turns state changes into events in a microtask.
 * @module
 */
import { ControlSender } from './ControlSender.js';
export type FrameHandler = (frame: import('./FrameReader.js').Frame) => number | void;
export type SnapshotSource = {
    pipes?: (now: number) => import('../diagnostics/snapshot.js').NexusPipeSnapshot[];
    largeMessages?: (now: number) => import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[];
};
export type ProgressProbe = (now: number, progressTimeout: number) => string | null;
/**
 * Handles one received frame type after the connection completed. Returns a disconnect reason to end the session
 * (the disconnect byte is sent), or nothing to continue. Must not keep `frame.body` past the call without copying.
 *
 * @callback FrameHandler
 * @param {import('./FrameReader.js').Frame} frame
 * @returns {number | void}
 */
/**
 * Contributes to the session's snapshot (`getSnapshot()`): the pipe layer supplies `pipes`, the invocation layer
 * `largeMessages`. `now` is the session clock's time.
 *
 * @typedef {object} SnapshotSource
 * @property {(now: number) => import('../diagnostics/snapshot.js').NexusPipeSnapshot[]} [pipes]
 * @property {(now: number) => import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]} [largeMessages]
 */
/**
 * Reports a lack of progress (§9): returns a description of what stalled, or null.
 *
 * @callback ProgressProbe
 * @param {number} now Clock time.
 * @param {number} progressTimeout
 * @returns {string | null}
 */
export declare class NexusSession {
    #private;
    /** Resolves true when the handshake completes, false when the session ends first. Never rejects. @type {Promise<boolean>} */
    ready: Promise<boolean>;
    /** Resolves with the disconnect reason once the session is `Disconnected`. Never rejects. @type {Promise<number>} */
    closed: Promise<number>;
    /** Session ID assigned by the server; null before the handshake completes. @type {bigint | null} */
    id: bigint | null;
    /** Why the session ended; `None` while it is open. @type {number} */
    disconnectReason: number;
    /** The peer's advertised `maxMessageSize`; 0 before the handshake. */
    peerMaxMessageSize: number;
    /** The peer's advertised `invocationCredits`; 0 before the handshake. */
    peerInvocationCredits: number;
    /** The server's advertised idle timeout, or null when it does not send one. @type {number | null} */
    idleTimeoutMs: number | null;
    /** The ping interval in use; 0 before the handshake. */
    pingInterval: number;
    /** Clock time the session was created, which is when its transport connected. */
    connectedAt: number;
    /** Clock time of the last complete frame received. */
    lastReceived: number;
    framesReceived: number;
    bytesReceived: number;
    /** Coalesced control replies (§5.5, §7.2, §6.3, §4.5, §8). @type {ControlSender} */
    controlSender: ControlSender;
    /**
     * Called synchronously on every state change. The client defers it to an event.
     * @type {((state: import('../contract/enums.js').ConnectionStateValue) => void) | null}
     */
    onStateChange: ((state: import('../contract/enums.js').ConnectionStateValue) => void) | null;
    /**
     * @param {object} init
     * @param {import('../transport/ITransport.js').Transport} init.transport A connected transport.
     * @param {import('./options.js').ResolvedClientOptions} init.options
     * @param {import('../contract/handshake.js').HandshakeDescriptor} init.handshake
     * @param {boolean} [init.isReconnecting] Sends `ClientGreetingReconnection` (101) instead of `ClientGreeting` (100).
     */
    constructor({ transport, options, handshake, isReconnecting }: {
        transport: import('../transport/ITransport.js').Transport;
        options: import('./options.js').ResolvedClientOptions;
        handshake: import('../contract/handshake.js').HandshakeDescriptor;
        isReconnecting?: boolean;
    });
    /** @returns {import('../contract/enums.js').ConnectionStateValue} */
    get state(): import('../contract/enums.js').ConnectionStateValue;
    /** True once the handshake completed. @returns {boolean} */
    get isHandshakeComplete(): boolean;
    /** True once a disconnect started. @returns {boolean} */
    get isDisconnecting(): boolean;
    /** The transport's own security flag. @returns {boolean} */
    get transportIsSecure(): boolean;
    /** @returns {unknown} */
    get remoteCertificate(): unknown;
    /** The resolved client options. @returns {import('./options.js').ResolvedClientOptions} */
    get options(): import('./options.js').ResolvedClientOptions;
    /** Largest value this side may send: the smaller of its own `maxMessageSize` and the peer's (§5). @returns {number} */
    get sendMaxMessageSize(): number;
    /** Frames and bytes handed to the transport. @returns {number} */
    get framesSent(): number;
    /** @returns {number} */
    get bytesSent(): number;
    /** Frames queued and not yet written. @returns {number} */
    get pendingWrites(): number;
    /**
     * Registers the handler of a message type received after the handshake. A type without a handler is a protocol
     * error. `Ping` and the greetings are handled by the session and can't be registered.
     * @param {number} type
     * @param {FrameHandler} handler
     * @returns {void}
     */
    setHandler(type: number, handler: FrameHandler): void;
    /**
     * Registers a listener called once, with the reason, when the session reaches `Disconnected`.
     * @param {(reason: number) => void} listener
     * @returns {void}
     */
    onClose(listener: (reason: number) => void): void;
    /**
     * Adds a progress probe checked on every keepalive tick (§9).
     * @param {ProgressProbe} probe
     * @returns {void}
     */
    addProgressProbe(probe: ProgressProbe): void;
    /**
     * Adds a snapshot source (the pipe and invocation layers register theirs when they attach).
     * @param {SnapshotSource} source
     * @returns {void}
     */
    addSnapshotSource(source: SnapshotSource): void;
    /**
     * The session-layer part of the snapshot (C# `NexusSessionSnapshot`): milliseconds since the transport connected,
     * since data was last written to the transport (null if nothing was), and how long the transport write in flight
     * has been running (null if none is).
     * @param {number} [now] Session clock time.
     * @returns {{ connected: number, sinceLastSent: number | null, flushInProgress: number | null }}
     */
    snapshot(now?: number): {
        connected: number;
        sinceLastSent: number | null;
        flushInProgress: number | null;
    };
    /**
     * Snapshots of the pipes holding a receive window (from the pipe layer).
     * @param {number} [now]
     * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot[]}
     */
    pipeSnapshots(now?: number): import('../diagnostics/snapshot.js').NexusPipeSnapshot[];
    /**
     * Snapshots of the arguments and results larger than one frame being received or sent (from the invocation layer).
     * @param {number} [now]
     * @returns {import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[]}
     */
    largeMessageSnapshots(now?: number): import('../diagnostics/snapshot.js').NexusLargeMessageSnapshot[];
    /**
     * Attaches a metrics sink (the metrics layer does, for sessions of tracked clients). Returns a function that
     * detaches it. Sinks are dropped when the session reaches `Disconnected`.
     * @param {import('./observers.js').MetricsSink} sink
     * @returns {() => void}
     */
    addMetricsSink(sink: import('./observers.js').MetricsSink): () => void;
    /**
     * Reports an event instrument measurement to the attached sinks (see {@link import('./observers.js').MetricsSink}).
     * Does nothing when none is attached.
     * @param {string} name A key of `Instruments`.
     * @param {number} value
     * @param {Readonly<Record<string, string>>} [tags]
     * @returns {void}
     */
    recordMetric(name: string, value: number, tags?: Readonly<Record<string, string>>): void;
    /**
     * Queues an encoded frame. Resolves true once written, false when the session ended first. Frames queued in the
     * same tick go out in one transport write.
     * @param {Uint8Array} frame
     * @returns {Promise<boolean>}
     */
    send(frame: Uint8Array): Promise<boolean>;
    /**
     * Starts the session: reads from the transport, sends the preamble and the greeting, and starts the handshake
     * timeout. Call once.
     * @param {Uint8Array | null} authenticationToken
     * @returns {void}
     */
    start(authenticationToken: Uint8Array | null): void;
    /**
     * Ends the session with `reason` and sends the disconnect byte (§5.6). Resolves once the session is `Disconnected`.
     * @param {number} [reason] Default `Graceful`.
     * @returns {Promise<void>}
     */
    disconnect(reason?: number): Promise<void>;
    /**
     * Ends the session without sending a disconnect byte (an aborted connect, a transport that ended).
     * @param {number} reason
     * @returns {Promise<void>}
     */
    abort(reason: number): Promise<void>;
    /**
     * Checks the idle timeout (§5.5) and the progress timeout (§9); disconnects and returns true when one expired.
     * Runs on every keepalive tick.
     * @returns {boolean}
     */
    checkTimeouts(): boolean;
}
