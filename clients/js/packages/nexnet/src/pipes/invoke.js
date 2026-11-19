/**
 * Sends a pipe or channel method (protocol spec §7.6): an IgnoreReturn | DuplexPipe invocation whose pipe parameter
 * is this side's local pipe ID.
 * @module
 */

import { serialize } from '../invocation/codec.js';
import { InvocationFlags } from '../invocation/messages.js';
import { NexusError } from '../contract/errors.js';
import { getPipeManager } from './PipeManager.js';

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
export async function invokePipe(invocations, session, methodId, pipe, args, signal) {
  const manager = getPipeManager(session);
  if (manager === null) throw new NexusError('The session has no pipe layer.');
  const core = manager.beginInvoke(pipe);
  try {
    const bytes = serialize((writer) => args(writer, core.localId));
    await invocations.invoke(methodId, bytes, { signal, flags: InvocationFlags.DuplexPipe, expectResult: false });
  } catch (error) {
    manager.invokeFailed(core, error);
    throw error;
  }
}
