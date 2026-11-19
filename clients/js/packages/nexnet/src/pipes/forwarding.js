/**
 * Byte-level relays between pipes (C# `NexusPipeForwardingExtensions`), for relay and gateway clients.
 * @module
 */

import { cancellationError } from '../contract/errors.js';

/**
 * Copies what `source` receives to `destination` until the source's input completes, then completes the
 * destination's writing direction. Reads one buffer at a time and sends only what the destination's credit allows
 * before consuming it, so the source's credit is granted back only as the destination drains.
 *
 * @param {import('./PipeCore.js').PipeCore} source
 * @param {import('./PipeCore.js').PipeCore} destination
 * @param {AbortSignal} [signal] Stops the forward; neither pipe is completed.
 * @param {{ halfClosed: boolean }} [state] Set before the destination is half-closed, so a bridge watching the
 *   destination's `completed` knows the completion is the normal end.
 * @returns {Promise<void>} Resolves when the source's input completed or the destination's output was completed.
 */
export async function forwardPipe(source, destination, signal, state = { halfClosed: false }) {
  for (;;) {
    if (signal?.aborted) throw cancellationError(signal);
    if (destination.outputDone) return;
    if (source.buffered > 0) {
      if (destination.sendCredit <= 0) {
        await destination.waitCredit(signal);
        continue;
      }
      const chunk = source.peekChunk();
      const n = Math.min(chunk.length, destination.sendCredit);
      // Sends without waiting (the credit is there); the bytes are copied into frames before it returns.
      const sent = destination.writeBytes(chunk.subarray(0, n));
      source.consume(n);
      try {
        await sent;
      } catch (error) {
        if (destination.outputDone && destination.failure === null) return;
        throw error;
      }
      continue;
    }
    if (source.inputDone) {
      state.halfClosed = true;
      await destination.completeWriting();
      return;
    }
    await source.waitInput(signal);
  }
}

/**
 * @typedef {{ key: 'ab' | 'ba' | 'a' | 'b', ok: boolean, error?: unknown }} Settled
 */

/**
 * @param {'ab' | 'ba' | 'a' | 'b'} key
 * @param {Promise<unknown>} promise
 * @returns {Promise<Settled>}
 */
function settled(key, promise) {
  return promise.then(() => ({ key, ok: true }), (error) => ({ key, ok: false, error }));
}

/**
 * Forwards `a` to `b` and `b` to `a` (C# `BridgeAsync`); see `NexusDuplexPipe.bridge`.
 *
 * @param {{ complete(): Promise<void>, completed: Promise<void> }} a
 * @param {{ complete(): Promise<void>, completed: Promise<void> }} b
 * @param {import('./PipeCore.js').PipeCore} coreA
 * @param {import('./PipeCore.js').PipeCore} coreB
 * @param {AbortSignal} [signal]
 * @returns {Promise<void>}
 */
export async function bridgePipes(a, b, coreA, coreB, signal) {
  const stop = new AbortController();
  const onAbort = () => stop.abort(signal?.reason);
  if (signal?.aborted) throw cancellationError(signal);
  signal?.addEventListener('abort', onAbort, { once: true });
  try {
    const ab = { halfClosed: false };
    const ba = { halfClosed: false };
    const aToB = forwardPipe(coreA, coreB, stop.signal, ab);
    const bToA = forwardPipe(coreB, coreA, stop.signal, ba);
    /** @type {Map<string, Promise<Settled>>} */
    const pending = new Map([
      ['ab', settled('ab', aToB)],
      ['ba', settled('ba', bToA)],
      ['a', settled('a', a.completed)],
      ['b', settled('b', b.completed)],
    ]);
    let abDone = false;
    let baDone = false;

    /** @type {Settled} */
    let ended;
    for (;;) {
      if (abDone && baDone) return;
      const result = await Promise.race(pending.values());
      pending.delete(result.key);
      if (result.key === 'ab' || result.key === 'ba') {
        if (result.ok) {
          if (result.key === 'ab') abDone = true;
          else baDone = true;
          continue;
        }
        ended = result;
        break;
      }
      // A pipe is complete. After a normal half-close the reverse direction may still be draining what it buffered,
      // so this is an abnormal end only while the direction writing to that pipe has not closed it.
      if ((result.key === 'a' ? ba : ab).halfClosed) continue;
      ended = result;
      break;
    }

    const forwardFailed = (ended.key === 'ab' || ended.key === 'ba') && !ended.ok;
    if (forwardFailed && signal?.aborted) {
      // The caller's cancellation: stop the other direction, complete neither pipe.
      stop.abort(signal.reason);
      await Promise.allSettled([aToB, bToA]);
      throw cancellationError(signal);
    }

    stop.abort();
    await Promise.allSettled([a.complete(), b.complete()]);
    await Promise.allSettled([aToB, bToA]);
    // A direction that threw: rethrow its error. A pipe that ended is a normal return.
    if (forwardFailed) throw ended.error;
  } finally {
    signal?.removeEventListener('abort', onAbort);
  }
}
