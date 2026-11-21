/**
 * Sends a pipe or channel method (protocol spec §7.6): an IgnoreReturn | DuplexPipe invocation whose pipe parameter
 * is this side's local pipe ID.
 * @module
 */
/**
 * Invokes a pipe method with `pipe` (from `createPipe`). Resolves once the invocation was sent; `pipe.ready` tells
 * whether the server opened the pipe. When the invocation can't be sent (canceled before send, not connected, too
 * large) it rejects, and the pipe fails with the same error and is freed.
 *
 * @param {import('../invocation/InvocationManager.js').InvocationManager} invocations The connected session's layer.
 * @param {import('../session/NexusSession.js').NexusSession | null} session
 * @param {number} methodId
 * @param {import('./NexusDuplexPipe.js').NexusDuplexPipe} pipe
 * @param {import('../client/ProxyBase.js').PipeArgumentsWriter} args
 * @param {AbortSignal | undefined} signal
 * @returns {Promise<void>}
 */
export declare function invokePipe(invocations: import('../invocation/InvocationManager.js').InvocationManager, session: import('../session/NexusSession.js').NexusSession | null, methodId: number, pipe: import('./NexusDuplexPipe.js').NexusDuplexPipe, args: import('../client/ProxyBase.js').PipeArgumentsWriter, signal: AbortSignal | undefined): Promise<void>;
