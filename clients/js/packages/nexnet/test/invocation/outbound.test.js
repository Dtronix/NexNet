import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { NexusMessageTooLargeError, NexusRemoteInvocationError, NexusTransportError, NexusUnauthorizedError, isCancellationError } from '../../src/contract/errors.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getClientSnapshot, getReservedInvocationIds } from '../../src/diagnostics/snapshot.js';
import { getInvocationCounters } from '../../src/invocation/diagnostics.js';
import { AbortReason, LargeMessageKind, ResultState, decodeInvocation, decodeMessageStart, encodeInvocationResult, encodeRemoteErrorDetail } from '../../src/invocation/messages.js';
import { MessageType } from '../../src/session/protocol.js';
import { decodeInvocationCancellation, decodeMessageAbort } from '../../src/session/wire.js';
import { connectedHarness, tick, until } from './support/harness.js';
import { binFormatter, intFormatter, pack, stringFormatter, testCodec } from './support/testCodec.js';

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
beforeEach(() => {
  previousCodec = setInvocationCodec(testCodec);
});
afterEach(() => {
  setInvocationCodec(previousCodec);
});

const zeroCounters = () => ({ pendingInvocations: 0, reservedInvocationIds: 0, creditWaiters: 0, inboundInvocations: 0, largeMessages: 0 });

/** Disconnects and checks the leak-audit counters. @param {Awaited<ReturnType<typeof connectedHarness>>} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getInvocationCounters()).toEqual(zeroCounters());
}

/** @param {Awaited<ReturnType<typeof connectedHarness>>} h */
const invocations = (h) => h.framesOf(MessageType.Invocation).map((f) => decodeInvocation(f.body));

describe('outbound invocations', () => {
  it('sends the C# Invocation layout and resolves the result through the formatter', async () => {
    const h = await connectedHarness();
    const call = h.proxy.invoke(5, (w) => { w.writeArrayHeader(2); w.writeInt(42); w.writeString('hi'); }, { returns: intFormatter });
    await tick();
    // Same bytes as the C# vector Invocation/result-int-string: [1, 5, 0, [42, "hi"]].
    expect(invocations(h)).toEqual([{ invocationId: 1, methodId: 5, flags: 0, args: pack((w) => { w.writeArrayHeader(2); w.writeInt(42); w.writeString('hi'); }) }]);
    expect(getInvocationCounters()).toMatchObject({ pendingInvocations: 1, reservedInvocationIds: 1 });
    h.sendResult(1, ResultState.CompletedResult, pack((w) => w.writeInt(84)));
    await expect(call).resolves.toBe(84);
    await finish(h);
  });

  it('ValueTask without a result resolves undefined; nil and missing results; no-arg methods send an empty array', async () => {
    const h = await connectedHarness();
    const a = h.proxy.invoke(0, null);
    const b = h.proxy.invoke(1, null, { returns: stringFormatter });
    const c = h.proxy.invoke(2, null, { returns: stringFormatter });
    await tick();
    expect(invocations(h).map((i) => [i.invocationId, i.args])).toEqual([[1, Uint8Array.of(0x90)], [2, Uint8Array.of(0x90)], [3, Uint8Array.of(0x90)]]);
    h.sendResult(1, ResultState.CompletedResult);
    h.sendResult(2, ResultState.CompletedResult, Uint8Array.of(0xc0));
    h.sendResult(3, ResultState.CompletedResult);
    await expect(a).resolves.toBeUndefined();
    await expect(b).resolves.toBeNull();
    await expect(c).rejects.toThrow(/carried no value/);
    await finish(h);
  });

  it('maps Exception, Unauthorized and Canceled results', async () => {
    const h = await connectedHarness();
    const calls = [1, 2, 3, 4, 5].map((m) => h.proxy.invoke(m, null));
    await tick();
    h.sendResult(1, ResultState.Exception);
    h.sendResult(2, ResultState.Exception, encodeRemoteErrorDetail({ code: 3, type: 'System.Exception', message: 'x' }));
    h.sendResult(3, ResultState.Exception, Uint8Array.of(0x92, 0xc0, 0xc0)); // wrong shape: no detail, not an error
    h.sendResult(4, ResultState.Unauthorized);
    h.sendResult(5, ResultState.Canceled);
    const results = await Promise.allSettled(calls);
    const reasons = results.map((r) => /** @type {PromiseRejectedResult} */ (r).reason);
    expect(reasons[0]).toBeInstanceOf(NexusRemoteInvocationError);
    expect(reasons[0].remoteType).toBeNull();
    expect(reasons[1]).toMatchObject({ code: 3, remoteType: 'System.Exception', remoteMessage: 'x' });
    expect(reasons[2]).toMatchObject({ name: 'NexusRemoteInvocationError', remoteMessage: null });
    expect(reasons[3]).toBeInstanceOf(NexusUnauthorizedError);
    expect(reasons[4]).toMatchObject({ name: 'AbortError' });
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('a result for an invocation that is not outstanding is a protocol error', async () => {
    const h = await connectedHarness();
    h.sendResult(9, ResultState.CompletedResult);
    await h.client.disconnected;
    expect(h.transport.sent.at(-1)).toBe(DisconnectReason.ProtocolError);
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('fire-and-forget sends IgnoreReturn with ID 0 and nothing waits for a result', async () => {
    const h = await connectedHarness();
    h.proxy.invokeVoid(3, (w) => { w.writeArrayHeader(1); w.writeInt(7); });
    await tick();
    expect(invocations(h)).toEqual([{ invocationId: 0, methodId: 3, flags: 1, args: pack((w) => { w.writeArrayHeader(1); w.writeInt(7); }) }]);
    expect(getInvocationCounters()).toEqual(zeroCounters());
    await finish(h);
  });

  it('rejects with NexusTransportError when not connected; fire-and-forget logs instead', async () => {
    const h = await connectedHarness();
    const proxy = h.proxy;
    await h.client.disconnect();
    await expect(proxy.invoke(0, null)).rejects.toBeInstanceOf(NexusTransportError);
    expect(proxy.invokeVoid(0, null)).toBeUndefined();
    expect(h.logs.some(([level, message]) => level === 'warning' && /Fire-and-forget invocation of method 0 failed/.test(message))).toBe(true);
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('arguments larger than the negotiated maximum fail before anything is sent', async () => {
    const h = await connectedHarness({ greeting: { maxMessageSize: 65526 } });
    const big = new Uint8Array(70_000);
    await expect(h.proxy.invoke(0, (w) => { w.writeArrayHeader(1); w.writeBin(big); })).rejects.toBeInstanceOf(NexusMessageTooLargeError);
    await tick();
    expect(invocations(h)).toEqual([]);
    expect(h.session.isDisconnecting).toBe(false);
    await finish(h);
  });
});

describe('invocation credits (§8)', () => {
  it('waits for credit in FIFO order, fire-and-forget included; credit returns let them out', async () => {
    const h = await connectedHarness({ greeting: { invocationCredits: 2 } });
    const a = h.proxy.invoke(0, null);
    h.proxy.invokeVoid(1, null);
    const c = h.proxy.invoke(2, null);
    h.proxy.invokeVoid(3, null);
    await tick();
    expect(invocations(h).map((i) => i.methodId)).toEqual([0, 1]);
    expect(getInvocationCounters().creditWaiters).toBe(2);
    h.sendCredit(1);
    await tick();
    expect(invocations(h).map((i) => i.methodId)).toEqual([0, 1, 2]);
    h.sendCredit(1);
    await tick();
    expect(invocations(h).map((i) => i.methodId)).toEqual([0, 1, 2, 3]);
    h.sendResult(1, ResultState.CompletedResult);
    h.sendResult(2, ResultState.CompletedResult);
    await Promise.all([a, c]);
    await finish(h);
  });

  it('zero credit or credit beyond what the server advertised is a flow-control error', async () => {
    for (const credits of [0, 3]) {
      const h = await connectedHarness({ greeting: { invocationCredits: 2 } });
      h.sendCredit(credits);
      await h.client.disconnected;
      expect(h.transport.sent.at(-1)).toBe(DisconnectReason.FlowControl);
    }
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('invocations queued before a disconnect go out before the byte; result calls end canceled (§5.6, K25)', async () => {
    const h = await connectedHarness();
    const calls = [h.proxy.invoke(0, null), h.proxy.invoke(1, null)];
    h.proxy.invokeVoid(3, null);
    const results = Promise.allSettled(calls);
    await h.client.disconnect();
    // As the invocations a C# client wrote before its disconnect: all of them, then the byte.
    expect(h.frames().map((f) => f.type)).toEqual([MessageType.Invocation, MessageType.Invocation, MessageType.Invocation, DisconnectReason.Graceful]);
    expect(invocations(h).map((i) => [i.invocationId, i.methodId, i.flags])).toEqual([[1, 0, 0], [2, 1, 0], [0, 3, 1]]);
    // The session ends before a result can arrive: canceled (C-2), as in C#.
    for (const result of await results) {
      expect(result.status).toBe('rejected');
      const error = /** @type {PromiseRejectedResult} */ (result).reason;
      expect(isCancellationError(error)).toBe(true);
      expect(error.cause).toBeInstanceOf(NexusTransportError);
      expect(error.cause.reason).toBe(DisconnectReason.Graceful);
    }
    await tick();
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('the session ending fails calls waiting for credit and calls waiting for results', async () => {
    const h = await connectedHarness({ greeting: { invocationCredits: 1 } });
    const sent = h.proxy.invoke(0, null);
    const waiting = h.proxy.invoke(1, null);
    await tick();
    h.transport.serverClose();
    // Amendment C-2: canceled (AbortError), with the transport error and its reason as the cause.
    for (const call of [sent, waiting]) {
      const error = await call.then(() => null, (e) => e);
      expect(error.name).toBe('AbortError');
      expect(isCancellationError(error)).toBe(true);
      expect(error.cause).toBeInstanceOf(NexusTransportError);
      expect(error.cause.reason).toBe(DisconnectReason.SocketError);
    }
    await tick();
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });
});

describe('cancellation (§4.5, D17)', () => {
  it('a signal already aborted rejects with its reason (or AbortError) and sends nothing', async () => {
    const h = await connectedHarness();
    const controller = new AbortController();
    controller.abort();
    await expect(h.proxy.invoke(0, null, { signal: controller.signal })).rejects.toMatchObject({ name: 'AbortError' });
    const custom = new AbortController();
    const reason = new Error('mine');
    custom.abort(reason);
    await expect(h.proxy.invoke(0, null, { signal: custom.signal })).rejects.toBe(reason);
    await tick();
    expect(invocations(h)).toEqual([]);
    expect(getInvocationCounters()).toEqual(zeroCounters());
    await finish(h);
  });

  it('a cancel while waiting for credit rejects, sends nothing and holds no ID or place', async () => {
    const h = await connectedHarness({ greeting: { invocationCredits: 1 } });
    const first = h.proxy.invoke(0, null);
    const controller = new AbortController();
    const canceled = h.proxy.invoke(1, null, { signal: controller.signal });
    const third = h.proxy.invoke(2, null);
    await tick();
    controller.abort();
    await expect(canceled).rejects.toMatchObject({ name: 'AbortError' });
    h.sendResult(1, ResultState.CompletedResult);
    h.sendCredit(1);
    await first;
    await tick();
    expect(invocations(h).map((i) => [i.invocationId, i.methodId])).toEqual([[1, 0], [2, 2]]);
    h.sendResult(2, ResultState.CompletedResult);
    await third;
    await finish(h);
  });

  it('a cancel after send rejects at once, sends InvocationCancellation and consumes the late result', async () => {
    const h = await connectedHarness();
    const controller = new AbortController();
    let added = 0;
    let removed = 0;
    const signal = controller.signal;
    const add = signal.addEventListener.bind(signal);
    const remove = signal.removeEventListener.bind(signal);
    signal.addEventListener = /** @type {any} */ ((/** @type {any} */ type, /** @type {any} */ listener, /** @type {any} */ options) => { added++; return add(type, listener, options); });
    signal.removeEventListener = /** @type {any} */ ((/** @type {any} */ type, /** @type {any} */ listener) => { removed++; return remove(type, listener); });

    const call = h.proxy.invoke(0, null, { signal });
    await tick();
    controller.abort();
    await expect(call).rejects.toMatchObject({ name: 'AbortError' });
    expect(added).toBe(1);
    expect(removed).toBe(1);
    await tick();
    expect(h.framesOf(MessageType.InvocationCancellation).map((f) => decodeInvocationCancellation(f.body))).toEqual([{ invocationId: 1 }]);
    // The ID stays reserved until the late result arrives; that result is not a protocol error.
    expect(getInvocationCounters()).toMatchObject({ pendingInvocations: 0, reservedInvocationIds: 1 });
    // The snapshot no longer awaits it, but the session still holds its ID (the interop client.outstanding view).
    expect(getClientSnapshot(h.client)?.invocations.awaitingResults).toBe(0);
    expect(getReservedInvocationIds(h.client)).toBe(1);
    h.sendResult(1, ResultState.Canceled);
    await tick();
    expect(h.session.isDisconnecting).toBe(false);
    expect(getInvocationCounters().reservedInvocationIds).toBe(0);
    expect(getReservedInvocationIds(h.client)).toBe(0);
    // A second result for it would be a protocol error now.
    h.sendResult(1, ResultState.CompletedResult);
    await h.client.disconnected;
    expect(h.transport.sent.at(-1)).toBe(DisconnectReason.ProtocolError);
  });

  it('removes the abort listener when the call completes normally', async () => {
    const h = await connectedHarness();
    const controller = new AbortController();
    let removed = 0;
    const remove = controller.signal.removeEventListener.bind(controller.signal);
    controller.signal.removeEventListener = /** @type {any} */ ((/** @type {any} */ type, /** @type {any} */ listener) => { removed++; return remove(type, listener); });
    const call = h.proxy.invoke(0, null, { signal: controller.signal, returns: intFormatter });
    await tick();
    h.sendResult(1, ResultState.CompletedResult, Uint8Array.of(5));
    await expect(call).resolves.toBe(5);
    expect(removed).toBe(1);
    controller.abort();
    await tick();
    expect(h.framesOf(MessageType.InvocationCancellation)).toEqual([]);
    await finish(h);
  });

  it('invocation IDs skip the ones still reserved', async () => {
    const h = await connectedHarness();
    const controller = new AbortController();
    const held = h.proxy.invoke(0, null, { signal: controller.signal });
    await tick();
    controller.abort();
    await held.catch(() => {});
    const next = h.proxy.invoke(0, null);
    await tick();
    expect(invocations(h).map((i) => i.invocationId)).toEqual([1, 2]);
    h.sendResult(2, ResultState.CompletedResult);
    await next;
    await finish(h);
  });
});

describe('large arguments and results (§6)', () => {
  /** @param {number} n */
  const blob = (n) => {
    const b = new Uint8Array(n);
    for (let i = 0; i < n; i++) b[i] = i & 0xff;
    return b;
  };
  /** @param {Uint8Array} payload */
  const binArgs = (payload) => (/** @type {import('../../src/contract/codec.js').MsgPackWriter} */ w) => { w.writeArrayHeader(1); w.writeBin(payload); };

  it('streams an argument larger than one frame as MessageStart + chunks, interleaved with other frames', async () => {
    const h = await connectedHarness();
    const payload = blob(150_000);
    const call = h.proxy.invoke(1, binArgs(payload), { returns: intFormatter });
    const small = h.proxy.invoke(2, null);
    await until(() => h.framesOf(MessageType.MessageChunk).length === 3);
    const frames = h.frames();
    const types = frames.map((f) => f.type);
    expect(types[0]).toBe(MessageType.MessageStart);
    // The small call goes out while the large argument streams.
    expect(types.indexOf(MessageType.Invocation)).toBeLessThan(types.lastIndexOf(MessageType.MessageChunk));
    const start = decodeMessageStart(frames[0].body);
    const chunks = h.framesOf(MessageType.MessageChunk);
    expect(chunks.map((c) => c.body.length)).toEqual([65535, 65535, start.totalLength - 131070]);
    expect(chunks.every((c) => c.invocationId === start.invocationId && c.kind === LargeMessageKind.Argument)).toBe(true);
    const body = new Uint8Array(start.totalLength);
    let o = 0;
    for (const c of chunks) { body.set(c.body, o); o += c.body.length; }
    const inner = decodeInvocation(body);
    expect([inner.invocationId, inner.methodId, inner.flags]).toEqual([start.invocationId, 1, 0]);
    expect(inner.args).toEqual(pack(/** @type {any} */ (binArgs(payload))));

    h.sendResult(start.invocationId, ResultState.CompletedResult, Uint8Array.of(9));
    h.sendResult(decodeInvocation(h.framesOf(MessageType.Invocation)[0].body).invocationId, ResultState.CompletedResult);
    await expect(call).resolves.toBe(9);
    await small;
    await finish(h);
  });

  it('a streamed fire-and-forget argument gets a unique ID for its stream and releases it once sent', async () => {
    const h = await connectedHarness();
    h.proxy.invokeVoid(3, binArgs(blob(70_000)));
    await until(() => h.framesOf(MessageType.MessageChunk).length === 2);
    await tick();
    const start = decodeMessageStart(h.framesOf(MessageType.MessageStart)[0].body);
    expect(start.invocationId).toBe(1);
    expect(getInvocationCounters()).toEqual(zeroCounters());
    await finish(h);
  });

  it('a refused argument fails its call (Busy, TooLarge) and the stream answers with Ack', async () => {
    for (const [reason, name] of [[AbortReason.Busy, 'NexusBusyError'], [AbortReason.TooLarge, 'NexusMessageTooLargeError']]) {
      const h = await connectedHarness({ config: {} });
      h.transport.holdWrites = true;
      const call = h.proxy.invoke(1, binArgs(blob(200_000)));
      await tick();
      h.transport.releaseWrites(); // MessageStart written
      await tick();
      h.sendAbort(1, LargeMessageKind.Argument, /** @type {number} */ (reason));
      await expect(call).rejects.toMatchObject({ name });
      h.transport.holdWrites = false;
      h.transport.releaseWrites();
      await until(() => h.framesOf(MessageType.MessageAbort).length === 1);
      expect(decodeMessageAbort(h.framesOf(MessageType.MessageAbort)[0].body)).toEqual({ invocationId: 1, kind: 0, reason: AbortReason.Ack });
      await tick();
      expect(h.framesOf(MessageType.MessageChunk).length).toBeLessThan(4);
      await finish(h);
    }
  });

  it('a cancel while the argument streams sends MessageAbort(Cancelled), no InvocationCancellation, and rejects', async () => {
    const h = await connectedHarness();
    h.transport.holdWrites = true;
    const controller = new AbortController();
    const call = h.proxy.invoke(1, binArgs(blob(200_000)), { signal: controller.signal });
    await tick();
    controller.abort();
    h.transport.holdWrites = false;
    h.transport.releaseWrites();
    await expect(call).rejects.toMatchObject({ name: 'AbortError' });
    await until(() => h.framesOf(MessageType.MessageAbort).length === 1);
    expect(decodeMessageAbort(h.framesOf(MessageType.MessageAbort)[0].body)).toEqual({ invocationId: 1, kind: 0, reason: AbortReason.Cancelled });
    expect(h.framesOf(MessageType.InvocationCancellation)).toEqual([]);
    expect(getInvocationCounters()).toEqual(zeroCounters());
    await finish(h);
  });

  it('the session ending while an argument streams rejects as canceled with the transport error as cause (C-2)', async () => {
    const h = await connectedHarness();
    h.transport.holdWrites = true;
    const call = h.proxy.invoke(1, binArgs(blob(200_000)));
    await tick();
    await h.session.abort(DisconnectReason.Timeout);
    const error = await call.then(() => null, (e) => e);
    expect(error.name).toBe('AbortError');
    expect(error.cause).toBeInstanceOf(NexusTransportError);
    expect(error.cause.reason).toBe(DisconnectReason.Timeout);
    await tick();
    expect(getInvocationCounters()).toEqual(zeroCounters());
  });

  it('reassembles a streamed result', async () => {
    const h = await connectedHarness();
    const payload = blob(100_000);
    const call = h.proxy.invoke(1, null, { returns: binFormatter });
    await tick();
    const body = encodeInvocationResult(1, ResultState.CompletedResult, pack((w) => w.writeBin(payload)));
    h.sendStart({ invocationId: 1, kind: LargeMessageKind.Result, innerType: MessageType.InvocationResult, totalLength: body.length });
    h.sendChunk(1, LargeMessageKind.Result, body.subarray(0, 1000));
    h.sendChunk(1, LargeMessageKind.Result, body.subarray(1000, 60_000));
    expect(getInvocationCounters().largeMessages).toBe(1);
    h.sendChunk(1, LargeMessageKind.Result, body.subarray(60_000));
    await expect(call).resolves.toEqual(payload);
    await finish(h);
  });

  it('a streamed result for an invocation that is not outstanding is a protocol error', async () => {
    const h = await connectedHarness();
    h.sendStart({ invocationId: 4, kind: LargeMessageKind.Result, innerType: MessageType.InvocationResult, totalLength: 10 });
    await h.client.disconnected;
    expect(h.transport.sent.at(-1)).toBe(DisconnectReason.ProtocolError);
  });
});
