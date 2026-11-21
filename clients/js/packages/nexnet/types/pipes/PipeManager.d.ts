/**
 * The pipe layer of one session (protocol spec §7, C# `NexusPipeManager`): local pipe IDs, receive windows reserved
 * from the session budget and counted against `maxPipes`, both roles of an open (this side opens a pipe with
 * `createPipe` and a pipe method; the server opens one by invoking a client method with a pipe parameter), the
 * `DuplexPipeWrite`, `PipeWindowUpdate` and `DuplexPipeUpdateState` frames, refusals, two-sided close and stalls.
 *
 * The client attaches one manager to every session before it starts ({@link attachPipes}), after the invocation layer,
 * whose dispatch contexts resolve pipe and channel parameters through {@link PipeManager#register}.
 * @module
 */
import { NexusDuplexChannel } from './channels.js';
import { NexusDuplexPipe } from './NexusDuplexPipe.js';
import { PipeCore } from './PipeCore.js';
/**
 * Attaches a pipe manager to a session that has not started yet, and installs the pipe hooks of its invocation layer.
 * @param {import('../session/NexusSession.js').NexusSession} session
 * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations
 * @returns {PipeManager}
 */
export declare function attachPipes(session: import('../session/NexusSession.js').NexusSession, invocations: import('../invocation/InvocationManager.js').InvocationManager): PipeManager;
/**
 * The pipe manager of a session, or null.
 * @param {import('../session/NexusSession.js').NexusSession | null | undefined} session
 * @returns {PipeManager | null}
 */
export declare function getPipeManager(session: import('../session/NexusSession.js').NexusSession | null | undefined): PipeManager | null;
/**
 * Creates a pipe on `session` to pass to a pipe method (`client.createPipe()`).
 * @param {import('../session/NexusSession.js').NexusSession | null} session
 * @returns {NexusDuplexPipe}
 * @throws {NexusTransportError} Not connected.
 * @throws {NexusBusyError} `maxPipes` or the session budget is reached.
 */
export declare function createPipe(session: import('../session/NexusSession.js').NexusSession | null): NexusDuplexPipe;
/**
 * Creates a channel on `session` to pass to a channel method (`client.createChannel(formatter)`).
 * @template T
 * @param {import('../session/NexusSession.js').NexusSession | null} session
 * @param {import('../contract/codec.js').Formatter<T>} formatter
 * @returns {NexusDuplexChannel<T>}
 */
export declare function createChannel<T>(session: import('../session/NexusSession.js').NexusSession | null, formatter: import('../contract/codec.js').Formatter<T>): NexusDuplexChannel<T>;
export declare class PipeManager {
    #private;
    /**
     * @param {import('../session/NexusSession.js').NexusSession} session
     * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations
     */
    constructor(session: import('../session/NexusSession.js').NexusSession, invocations: import('../invocation/InvocationManager.js').InvocationManager);
    /** True while pipes can be opened. @returns {boolean} */
    get isOpen(): boolean;
    /** Pipes holding a receive window. @returns {number} */
    get openPipes(): number;
    /**
     * Snapshots of the pipes the session still tracks: open, being opened, or closing until both sides confirmed
     * (C# `NexusPipeManager.AddSnapshots`).
     * @param {number} now Session clock time.
     * @returns {import('../diagnostics/snapshot.js').NexusPipeSnapshot[]}
     */
    snapshots(now: number): import('../diagnostics/snapshot.js').NexusPipeSnapshot[];
    /**
     * Opening side: reserves a window and a local ID for a new pipe (C# `RentPipe`).
     * @returns {NexusDuplexPipe}
     */
    rentPipe(): NexusDuplexPipe;
    /**
     * Registering side: the server invoked a client method with a pipe parameter (C# `RegisterPipe`). Reserves a
     * window and a local ID and sends Ready with the window; refuses with `Refused` when a limit is reached. Frees the
     * invocation's execution slot (§8); the pipe is released when the method returns.
     *
     * @param {number} otherId The server's local pipe ID from the arguments.
     * @param {import('../contract/nexus.js').DispatchContext} ctx
     * @returns {NexusDuplexPipe}
     * @throws {NexusBusyError} Refused: `maxPipes` or the session budget is reached.
     */
    register(otherId: number, ctx: import('../contract/nexus.js').DispatchContext): NexusDuplexPipe;
    /**
     * Opening side: marks a pipe as sent with a pipe method. Throws when it can't be.
     * @param {NexusDuplexPipe} pipe
     * @returns {PipeCore}
     */
    beginInvoke(pipe: NexusDuplexPipe): PipeCore;
    /**
     * Opening side: the pipe method was not sent, so the pipe fails and is freed.
     * @param {PipeCore} core
     * @param {unknown} error
     */
    invokeFailed(core: PipeCore, error: unknown): void;
    /**
     * The application is done with a pipe (dispose, or the registered method returned): sends this side's Complete if
     * needed and frees the pipe once the peer has closed it too (C# `ReleasePipe`). A pipe the peer never saw is freed
     * at once; one whose open is in flight closes as soon as Ready arrives.
     * @param {PipeCore} core
     * @returns {Promise<void>}
     */
    release(core: PipeCore): Promise<void>;
}
