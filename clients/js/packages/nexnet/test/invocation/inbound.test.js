import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DisconnectReason, RemoteExceptionDetail } from '../../src/contract/enums.js';
import { NexusBusyError } from '../../src/contract/errors.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationCounters } from '../../src/invocation/diagnostics.js';
import { getInvocationManager, releaseInvocationSlot } from '../../src/invocation/InvocationManager.js';
import {
  AbortReason,
  InvocationFlags,
  LargeMessageKind,
  ResultState,
  decodeInvocationResult,
  decodeMessageStart,
  encodeInvocation,
  tryReadRemoteErrorDetail,
} from '../../src/invocation/messages.js';
import { encodeFrame } from '../../src/session/frames.js';
import { MessageType } from '../../src/session/protocol.js';
import { decodeInvocationCredit, decodeMessageAbort } from '../../src/session/wire.js';
import { connectedHarness, tick, until } from './support/harness.js';
import { pack, stringFormatter, testCodec } from './support/testCodec.js';

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
beforeEach(() => {
  previousCodec = setInvocationCodec(testCodec);
});
afterEach(() => {
  setInvocationCodec(previousCodec);
});

const zeroCounters = () => ({ pendingInvocations: 0, reservedInvocationIds: 0, creditWaiters: 0, inboundInvocations: 0, largeMessages: 0 });

/** @param {Awaited<ReturnType<typeof connectedHarness>>} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getInvocationCounters()).toEqual(zeroCounters());
}

/** @param {Awaited<ReturnType<typeof connectedHarness>>} h */
const results = (h) => h.framesOf(MessageType.InvocationResult).map((f) => decodeInvocationResult(f.body));

/** A promise and its resolver. */
function deferred() {
  /** @type {(value?: unknown) => void} */
  let resolve = () => {};
  /** @type {Promise<unknown>} */
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
}

const args1 = (/** @type {string} */ s) => pack((w) => { w.writeArrayHeader(1); w.writeString(s); });

describe('server-to-client invocations', () => {
  it('dispatches from a microtask with the reader at the arguments, and sends the result', async () => {
    const h = await connectedHarness();
    h.nexus.methods.set(1, (reader, ctx) => {
      reader.readArrayHeader();
      const text = stringFormatter.read(reader);
      ctx.writeResult(stringFormatter, String(text).toUpperCase());
    });
    h.sendInvocation(7, 1, 0, args1('hi'));
    // Never re-entrantly from the data callback.
    expect(h.nexus.dispatched).toEqual([]);
    await until(() => results(h).length === 1);
    expect(h.nexus.dispatched.map((d) => [d.methodId, d.invocationId])).toEqual([[1, 7]]);
    expect(results(h)).toEqual([{ invocationId: 7, state: ResultState.CompletedResult, value: pack((w) => w.writeString('HI')) }]);
    await finish(h);
  });

  it('handles an invocation that arrives in the same read as the ServerGreeting; the nexus context reaches the proxy', async () => {
    let calledBack = false;
    /** @type {bigint | null} */
    let sessionId = null;
    const h = await connectedHarness({
      afterHello: encodeFrame(MessageType.Invocation, encodeInvocation(1, 0, 0, null)),
      setup: (nexus) => nexus.methods.set(0, async () => {
        sessionId = nexus.context.sessionId;
        await nexus.context.proxy.invoke(1, null);
        calledBack = true;
      }),
    });
    await until(() => h.nexus.dispatched.length === 1);
    expect(h.session.isDisconnecting).toBe(false);
    await until(() => h.framesOf(MessageType.Invocation).length === 1);
    h.sendResult(1, ResultState.CompletedResult);
    await until(() => results(h).length === 1);
    expect(calledBack).toBe(true);
    expect(sessionId).toBe(7n);
    await finish(h);
  });

  it('a ValueTask method sends [id, CompletedResult]; IgnoreReturn sends nothing, even when it throws', async () => {
    const h = await connectedHarness();
    h.nexus.methods.set(0, () => {});
    h.nexus.methods.set(2, () => { throw new Error('void boom'); });
    h.sendInvocation(3, 0, 0, null);
    h.sendInvocation(0, 2, InvocationFlags.IgnoreReturn, null);
    await until(() => h.nexus.dispatched.length === 2);
    await tick();
    expect(results(h)).toEqual([{ invocationId: 3, state: ResultState.CompletedResult, value: null }]);
    expect(h.logs.some(([level, message]) => level === 'error' && /method 2 threw/.test(message))).toBe(true);
    await finish(h);
  });

  it('a method ID the client nexus does not accept is a protocol error (Set and BigUint64Array forms)', async () => {
    const h = await connectedHarness({ acceptedIds: new Set([0]) });
    h.sendInvocation(1, 5, 0, null);
    await h.client.disconnected;
    expect(h.transport.sent.at(-1)).toBe(DisconnectReason.ProtocolError);
    expect(h.nexus.dispatched).toEqual([]);

    // C# keys: (clientNexusHash << 16) | methodId; plain IDs work too.
    const key = BigInt.asUintN(64, BigInt(401298355) << 16n) | 4n;
    const k = await connectedHarness({ acceptedIds: BigUint64Array.of(key, 2n) });
    k.nexus.methods.set(4, () => {});
    k.nexus.methods.set(2, () => {});
    k.sendInvocation(1, 4, 0, null);
    k.sendInvocation(2, 2, 0, null);
    await until(() => results(k).length === 2);
    k.sendInvocation(3, 3, 0, null);
    await k.client.disconnected;
    expect(k.transport.sent.at(-1)).toBe(DisconnectReason.ProtocolError);
    await tick();
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('an invocation beyond the advertised credits is a flow-control error', async () => {
    const h = await connectedHarness({ config: { invocationCredits: 2, maxConcurrentConnectionInvocations: 1 } });
    const gate = deferred();
    h.nexus.methods.set(0, () => gate.promise);
    // One runs (its credit returns at dequeue), two queue: 1 + 2 credits in use is fine; a fourth is one too many.
    for (let id = 1; id <= 3; id++) h.sendInvocation(id, 0, 0, null);
    await tick();
    expect(h.session.isDisconnecting).toBe(false);
    h.sendInvocation(4, 0, 0, null);
    await h.client.disconnected;
    expect(h.transport.sent.at(-1)).toBe(DisconnectReason.FlowControl);
    gate.resolve();
    await tick();
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('returns invocation credit at dequeue for execution, in batches of half the advertised credits', async () => {
    const h = await connectedHarness({ config: { invocationCredits: 4, maxConcurrentConnectionInvocations: 1 } });
    const gates = [deferred(), deferred(), deferred()];
    let n = 0;
    h.nexus.methods.set(0, () => gates[n++].promise);
    for (let id = 1; id <= 3; id++) h.sendInvocation(id, 0, 0, null);
    await tick();
    // Only the first is dequeued: one credit owed, below the threshold of 2.
    expect(h.nexus.dispatched.length).toBe(1);
    expect(h.session.controlSender.pendingInvocationCredit).toBe(1);
    expect(h.framesOf(MessageType.InvocationCredit)).toEqual([]);
    gates[0].resolve();
    await until(() => h.nexus.dispatched.length === 2);
    await tick();
    expect(h.framesOf(MessageType.InvocationCredit).map((f) => decodeInvocationCredit(f.body))).toEqual([{ credits: 2 }]);
    gates[1].resolve();
    gates[2].resolve();
    await until(() => results(h).length === 3);
    await finish(h);
  });

  it('limits concurrency per pool; the separate pool does not wait on the normal one', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 2, maxSeparatePoolInvocations: 1 } });
    const gate = deferred();
    let running = 0;
    let peak = 0;
    h.nexus.methods.set(0, async () => {
      running++;
      peak = Math.max(peak, running);
      await gate.promise;
      running--;
    });
    h.nexus.methods.set(1, () => {});
    h.nexus.separate.add(1);
    for (let id = 1; id <= 4; id++) h.sendInvocation(id, 0, 0, null);
    h.sendInvocation(10, 1, 0, null);
    h.sendInvocation(11, 1, 0, null);
    await until(() => results(h).length === 2);
    expect(results(h).map((r) => r.invocationId)).toEqual([10, 11]);
    expect(getInvocationManager(h.session)?.inboundCount).toBe(4);
    gate.resolve();
    await until(() => results(h).length === 6);
    expect(peak).toBe(2);
    await finish(h);
  });

  it('warns when an invocation waited longer than invocationQueueWarningThreshold for a slot', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 1, invocationQueueWarningThreshold: 100 } });
    const gate = deferred();
    h.nexus.methods.set(0, () => gate.promise);
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 0, 0, null);
    await tick();
    h.clock.advance(150);
    gate.resolve();
    await until(() => results(h).length === 2);
    expect(h.logs.some(([level, message]) => level === 'warning' && /waited 150 ms for an execution slot \(maxConcurrentConnectionInvocations 1, threshold 100 ms\)/.test(message))).toBe(true);
    await finish(h);
  });

  it('InvocationCancellation aborts ctx.signal; a method that stops on it sends Canceled', async () => {
    const h = await connectedHarness();
    h.nexus.methods.set(0, (_reader, ctx) => new Promise((_resolve, reject) => {
      ctx.signal.addEventListener('abort', () => reject(ctx.signal.reason));
    }));
    h.sendInvocation(5, 0, 0, null);
    await until(() => h.nexus.dispatched.length === 1);
    h.sendCancellation(5);
    await until(() => results(h).length === 1);
    expect(results(h)).toEqual([{ invocationId: 5, state: ResultState.Canceled, value: null }]);
    // A cancellation for an invocation that already completed is ignored.
    h.sendCancellation(5);
    await tick();
    expect(h.session.isDisconnecting).toBe(false);
    await finish(h);
  });

  it('a queued invocation canceled before it starts runs with its signal already aborted', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 1 } });
    const gate = deferred();
    h.nexus.methods.set(0, () => gate.promise);
    h.nexus.methods.set(1, (_reader, ctx) => { ctx.signal.throwIfAborted(); });
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 1, 0, null);
    h.sendCancellation(2);
    gate.resolve();
    await until(() => results(h).length === 2);
    expect(h.nexus.dispatched[1].signal.aborted).toBe(true);
    expect(results(h)[1]).toEqual({ invocationId: 2, state: ResultState.Canceled, value: null });
    await finish(h);
  });

  it('exceptions: no detail by default, type and message with TypeAndMessage, the nexus hook overrides both', async () => {
    const plain = await connectedHarness();
    plain.nexus.methods.set(0, () => { throw new TypeError('bad'); });
    plain.sendInvocation(1, 0, 0, null);
    await until(() => results(plain).length === 1);
    expect(results(plain)).toEqual([{ invocationId: 1, state: ResultState.Exception, value: null }]);
    await finish(plain);

    const detailed = await connectedHarness({ config: { remoteExceptionDetail: RemoteExceptionDetail.TypeAndMessage } });
    detailed.nexus.methods.set(0, () => { throw new TypeError('bad'); });
    detailed.nexus.methods.set(1, () => { throw new NexusBusyError('x'.repeat(3000)); });
    detailed.sendInvocation(1, 0, 0, null);
    detailed.sendInvocation(2, 1, 0, null);
    await until(() => results(detailed).length === 2);
    const [r1, r2] = results(detailed);
    expect(tryReadRemoteErrorDetail(r1.value)).toEqual({ code: null, type: 'TypeError', message: 'bad' });
    expect(tryReadRemoteErrorDetail(r2.value)).toEqual({ code: null, type: 'NexusBusyError', message: 'x'.repeat(2048) });
    expect(detailed.logs.some(([level, message]) => level === 'warning' && /Method 1 failed/.test(message))).toBe(true);
    await finish(detailed);

    const hooked = await connectedHarness({ config: { remoteExceptionDetail: RemoteExceptionDetail.TypeAndMessage } });
    hooked.nexus.methods.set(0, () => { throw new Error('secret'); });
    hooked.nexus.methods.set(1, () => { throw new Error('other'); });
    hooked.nexus.exceptionHook = (error, methodId) => {
      if (methodId === 1) throw new Error('hook failed');
      return { code: 42, message: 'try again' };
    };
    hooked.sendInvocation(1, 0, 0, null);
    hooked.sendInvocation(2, 1, 0, null);
    await until(() => results(hooked).length === 2);
    expect(tryReadRemoteErrorDetail(results(hooked)[0].value)).toEqual({ code: 42, type: null, message: 'try again' });
    // A throwing hook is logged; the configured detail is sent.
    expect(tryReadRemoteErrorDetail(results(hooked)[1].value)).toEqual({ code: null, type: 'Error', message: 'other' });
    expect(hooked.logs.some(([level, message]) => level === 'error' && /onInvocationException threw/.test(message))).toBe(true);
    await finish(hooked);
  });

  it('streams a result larger than one frame; one beyond the negotiated maximum becomes an Exception', async () => {
    const h = await connectedHarness({ config: { remoteExceptionDetail: RemoteExceptionDetail.TypeAndMessage }, greeting: { maxMessageSize: 100_000 } });
    h.nexus.methods.set(0, (_r, ctx) => ctx.writeResult(stringFormatter, 'y'.repeat(80_000)));
    h.nexus.methods.set(1, (_r, ctx) => ctx.writeResult(stringFormatter, 'z'.repeat(200_000)));
    h.sendInvocation(1, 0, 0, null);
    await until(() => h.framesOf(MessageType.MessageChunk).length === 2);
    const start = decodeMessageStart(h.framesOf(MessageType.MessageStart)[0].body);
    expect(start).toMatchObject({ invocationId: 1, kind: LargeMessageKind.Result, innerType: MessageType.InvocationResult });
    const body = new Uint8Array(start.totalLength);
    let o = 0;
    for (const c of h.framesOf(MessageType.MessageChunk)) { body.set(c.body, o); o += c.body.length; }
    expect(decodeInvocationResult(body)).toEqual({ invocationId: 1, state: ResultState.CompletedResult, value: pack((w) => w.writeString('y'.repeat(80_000))) });

    h.sendInvocation(2, 1, 0, null);
    await until(() => results(h).length === 1);
    expect(results(h)[0].state).toBe(ResultState.Exception);
    expect(tryReadRemoteErrorDetail(results(h)[0].value)?.type).toBe('NexusMessageTooLargeError');
    await finish(h);
  });

  it('reassembles a streamed argument; one without budget is refused with Busy and its credit returns', async () => {
    const h = await connectedHarness({ config: { sessionBudget: 20 * 1024 * 1024, invocationCredits: 32 } });
    /** @type {number[]} */
    const lengths = [];
    h.nexus.methods.set(0, (reader) => { reader.readArrayHeader(); lengths.push(/** @type {Uint8Array} */ (reader.readBin()).length); });
    const body = encodeInvocation(9, 0, 0, pack((w) => { w.writeArrayHeader(1); w.writeBin(new Uint8Array(100_000)); }));
    h.sendStart({ invocationId: 9, kind: LargeMessageKind.Argument, innerType: MessageType.Invocation, totalLength: body.length });
    h.sendChunk(9, LargeMessageKind.Argument, body.subarray(0, 50_000));
    h.sendChunk(9, LargeMessageKind.Argument, body.subarray(50_000));
    await until(() => results(h).length === 1);
    expect(lengths).toEqual([100_000]);
    const manager = getInvocationManager(h.session);
    // Only the invocation-queue reservation is left.
    expect(manager?.budget.reserved).toBe(32 * 64 * 1024);

    // Fill the budget so the next stream can't be reserved.
    expect(manager?.budget.tryReserve(20 * 1024 * 1024 - 32 * 64 * 1024 - 10)).toBe(true);
    h.sendStart({ invocationId: 10, kind: LargeMessageKind.Argument, innerType: MessageType.Invocation, totalLength: body.length });
    await tick();
    expect(h.framesOf(MessageType.MessageAbort).map((f) => decodeMessageAbort(f.body))).toEqual([{ invocationId: 10, kind: 0, reason: AbortReason.Busy }]);
    h.sendChunk(10, LargeMessageKind.Argument, body.subarray(0, 60_000));
    h.sendChunk(10, LargeMessageKind.Argument, body.subarray(60_000));
    await tick();
    expect(lengths.length).toBe(1);
    expect(h.session.isDisconnecting).toBe(false);
    await finish(h);
  });

  it('the session ending aborts running invocations and sends no result', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 1 } });
    /** @type {AbortSignal | null} */
    let seen = null;
    const gate = deferred();
    h.nexus.methods.set(0, async (_r, ctx) => {
      seen = ctx.signal;
      await gate.promise;
    });
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 0, 0, null); // queued: dropped
    h.sendInvocation(0, 0, InvocationFlags.IgnoreReturn, null); // queued: dropped
    await until(() => seen !== null);
    expect(getInvocationCounters().inboundInvocations).toBe(3);
    h.transport.serverClose();
    await h.client.disconnected;
    expect(/** @type {AbortSignal | null} */ (seen)?.aborted).toBe(true);
    gate.resolve();
    await tick();
    expect(results(h)).toEqual([]);
    expect(h.nexus.dispatched.length).toBe(1);
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('overlapping fire-and-forget invocations share one signal; only a client disconnect aborts it', async () => {
    const h = await connectedHarness();
    /** @type {AbortSignal[]} */
    const signals = [];
    const gate = deferred();
    let calls = 0;
    h.nexus.methods.set(0, async (_r, ctx) => {
      signals.push(ctx.signal);
      // The first call runs until the session ends; the second returns at once.
      if (calls++ === 0) await gate.promise;
    });
    h.sendInvocation(0, 0, InvocationFlags.IgnoreReturn, null);
    await until(() => signals.length === 1);
    h.sendInvocation(0, 0, InvocationFlags.IgnoreReturn, null);
    await until(() => signals.length === 2);
    await tick();
    expect(signals[1]).toBe(signals[0]);
    // The second finishing does not abort the shared signal.
    expect(signals[0].aborted).toBe(false);
    expect(getInvocationCounters().inboundInvocations).toBe(1);

    await h.client.disconnect();
    expect(signals[0].aborted).toBe(true);
    gate.resolve();
    await tick();
    expect(results(h)).toEqual([]);
    expect(getInvocationCounters()).toEqual(zeroCounters());

    // A new session gets a fresh signal.
    const k = await connectedHarness();
    /** @type {AbortSignal | null} */
    let next = null;
    k.nexus.methods.set(0, (_r, ctx) => { next = ctx.signal; });
    k.sendInvocation(0, 0, InvocationFlags.IgnoreReturn, null);
    await until(() => next !== null);
    expect(/** @type {AbortSignal | null} */ (next)?.aborted).toBe(false);
    await finish(k);
  });

  it('a pipe parameter registers through the pipe layer (J08); releaseInvocationSlot frees the slot early', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 1 } });
    const gate = deferred();
    /** @type {unknown} */
    let pipeError = null;
    h.nexus.methods.set(0, async (_r, ctx) => {
      try { ctx.pipe(1); } catch (e) { pipeError = e; }
      releaseInvocationSlot(ctx);
      releaseInvocationSlot(ctx);
      await gate.promise;
    });
    h.nexus.methods.set(1, () => {});
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 1, 0, null);
    await until(() => results(h).length === 1);
    expect(results(h)[0].invocationId).toBe(2);
    expect(pipeError).toBeNull();
    gate.resolve();
    await until(() => results(h).length === 2);
    await finish(h);
  });
});
