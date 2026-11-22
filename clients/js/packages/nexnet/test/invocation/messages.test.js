import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { CreditGate } from '../../src/invocation/CreditGate.js';
import { LargeMessageReceiver } from '../../src/invocation/LargeMessageReceiver.js';
import {
  AbortReason,
  LargeMessageKind,
  decodeInvocation,
  decodeInvocationResult,
  decodeMessageStart,
  encodeInvocation,
  encodeInvocationResult,
  encodeMessageStart,
  encodeRemoteErrorDetail,
  skipValue,
  truncate,
  tryReadRemoteErrorDetail,
} from '../../src/invocation/messages.js';
import { remoteInvocationError } from '../../src/invocation/InvocationManager.js';
import { SessionBudget } from '../../src/invocation/SessionBudget.js';
import { WireFormatError } from '../../src/session/wire.js';
import { fromHex, toHex } from '../session/support/fixtures.js';

/** C#-recorded bodies (fixtures/recorder). @type {Array<{ name: string, type: number, fields: any, body: string }>} */
const vectors = readFileSync(new URL('./fixtures/invocations.jsonl', import.meta.url), 'utf8')
  .split(/\r?\n/).filter((l) => l.trim() !== '').map((l) => JSON.parse(l));
/** @param {string} prefix */
const byPrefix = (prefix) => vectors.filter((v) => v.name.startsWith(prefix));

describe('C# golden bodies (fixtures/invocations.jsonl)', () => {
  it('has every kind of vector', () => {
    expect(byPrefix('Invocation/').length).toBeGreaterThanOrEqual(5);
    expect(byPrefix('InvocationResult/').length).toBeGreaterThanOrEqual(10);
    expect(byPrefix('MessageStart/').length).toBe(3);
    expect(byPrefix('RemoteErrorDetail/').length).toBe(5);
  });

  for (const v of byPrefix('Invocation/')) {
    it(`${v.name}: encodes and decodes like C#`, () => {
      const f = v.fields;
      expect(toHex(encodeInvocation(f.invocationId, f.methodId, f.flags, fromHex(f.args)))).toBe(v.body);
      expect(decodeInvocation(fromHex(v.body))).toEqual({ invocationId: f.invocationId, methodId: f.methodId, flags: f.flags, args: fromHex(f.args) });
    });
  }

  it('null arguments encode as an empty array', () => {
    const v = vectors.find((x) => x.name === 'Invocation/ignore-return-no-args');
    expect(toHex(encodeInvocation(0, 0, 1, null))).toBe(v?.body);
  });

  for (const v of byPrefix('InvocationResult/')) {
    it(`${v.name}: encodes and decodes like C#`, () => {
      const f = v.fields;
      const value = f.value === null ? null : fromHex(f.value);
      expect(toHex(encodeInvocationResult(f.invocationId, f.state, value))).toBe(v.body);
      expect(decodeInvocationResult(fromHex(v.body))).toEqual({ invocationId: f.invocationId, state: f.state, value });
    });
  }

  for (const v of byPrefix('MessageStart/')) {
    it(`${v.name}: encodes and decodes like C#`, () => {
      expect(toHex(encodeMessageStart(v.fields))).toBe(v.body);
      expect(decodeMessageStart(fromHex(v.body))).toEqual(v.fields);
    });
  }

  for (const v of byPrefix('RemoteErrorDetail/')) {
    it(`${v.name}: writes and reads the detail like C#`, () => {
      const { code, type, message } = v.fields;
      expect(toHex(encodeRemoteErrorDetail({ code, type, message }))).toBe(v.body);
      const read = tryReadRemoteErrorDetail(fromHex(v.body));
      expect(read).toEqual({ code, type: truncate(type, 256), message: truncate(message, 2048) });
    });
  }

  it('the exception results read as NexusRemoteInvocationError like C# ProxyRemoteInvocationException', () => {
    const typed = vectors.find((x) => x.name === 'InvocationResult/exception-type-message');
    const e1 = remoteInvocationError(tryReadRemoteErrorDetail(fromHex(typed?.fields.value)));
    expect(e1.message).toBe('Exception occurred while executing method on proxy. System.InvalidOperationException: boom');
    expect([e1.code, e1.remoteType, e1.remoteMessage]).toEqual([0, 'System.InvalidOperationException', 'boom']);

    const app = vectors.find((x) => x.name === 'InvocationResult/exception-app-error');
    const e2 = remoteInvocationError(tryReadRemoteErrorDetail(fromHex(app?.fields.value)));
    expect(e2.message).toBe('Exception occurred while executing method on proxy. app error (code 17)');
    expect([e2.code, e2.remoteType, e2.remoteMessage]).toEqual([17, null, 'app error']);

    const none = remoteInvocationError(null);
    expect(none.message).toBe('Exception occurred while executing method on proxy.');
  });
});

describe('message bodies', () => {
  it('decoding rejects malformed bodies', () => {
    expect(() => decodeInvocation(Uint8Array.of(0x93, 0, 0, 0))).toThrow(WireFormatError);
    expect(() => decodeInvocation(Uint8Array.of(0x94, 0, 0, 0, 0x91))).toThrow(WireFormatError);
    expect(() => decodeInvocation(Uint8Array.of(0x94, 0, 0, 0, 0x90, 0x01))).toThrow(/trailing/);
    expect(() => decodeInvocationResult(Uint8Array.of(0x94, 0, 0, 0, 0))).toThrow(WireFormatError);
    expect(() => decodeInvocationResult(Uint8Array.of(0x92, 0, 1, 0))).toThrow(/trailing/);
    expect(() => decodeMessageStart(Uint8Array.of(0x93, 0, 0, 0))).toThrow(WireFormatError);
  });

  it('skipValue walks deep nesting without recursion and bounds-checks lengths', () => {
    const deep = new Uint8Array(10_001);
    deep.fill(0x91, 0, 10_000);
    deep[10_000] = 0xc0;
    expect(skipValue(deep, 0)).toBe(10_001);
    expect(() => skipValue(Uint8Array.of(0xdd, 0xff, 0xff, 0xff, 0xff), 0)).toThrow(WireFormatError);
    expect(() => skipValue(Uint8Array.of(0xc6, 0, 0, 0, 9, 1), 0)).toThrow(WireFormatError);
    expect(() => skipValue(Uint8Array.of(0xc1), 0)).toThrow(/invalid format/);
    expect(skipValue(Uint8Array.of(0x81, 0xa1, 0x6b, 0xd7, 1, 1, 2, 3, 4, 5, 6, 7, 8), 0)).toBe(13);
  });

  it('remote error detail: other shapes read as no detail', () => {
    expect(tryReadRemoteErrorDetail(null)).toBeNull();
    expect(tryReadRemoteErrorDetail(Uint8Array.of(0x92, 0xc0, 0xc0))).toBeNull();
    expect(tryReadRemoteErrorDetail(Uint8Array.of(0x93, 0xcf, 0, 0, 0, 1, 0, 0, 0, 0, 0xc0, 0xc0))).toBeNull(); // code outside int32
    expect(tryReadRemoteErrorDetail(Uint8Array.of(0x93, 0xc0, 0xc0, 0xc0, 0xc0))).toBeNull(); // trailing
    const longType = encodeRemoteErrorDetail({ code: null, type: null, message: null });
    expect(tryReadRemoteErrorDetail(longType)).toEqual({ code: null, type: null, message: null });
  });

  it('truncate never splits a surrogate pair', () => {
    expect(truncate('ab\u{1F600}', 3)).toBe('ab');
    expect(truncate('abc', 3)).toBe('abc');
    expect(truncate(null, 3)).toBeNull();
  });
});

describe('CreditGate', () => {
  it('serves waiters in FIFO order and hands released credit to them first', async () => {
    const gate = new CreditGate();
    gate.release(1);
    expect(gate.tryTake()).toBe(true);
    expect(gate.tryTake()).toBe(false);
    /** @type {number[]} */
    const order = [];
    const a = gate.wait().then((ok) => order.push(ok ? 1 : -1));
    const b = gate.wait().then((ok) => order.push(ok ? 2 : -2));
    expect(gate.waiting).toBe(2);
    gate.release(1);
    expect(gate.available).toBe(0);
    gate.release(2);
    await Promise.all([a, b]);
    expect(order).toEqual([1, 2]);
    expect(gate.available).toBe(1);
  });

  it('a canceled wait gives up its place; close ends every wait with false', async () => {
    const gate = new CreditGate();
    const controller = new AbortController();
    const canceled = gate.wait(controller.signal);
    const next = gate.wait();
    controller.abort();
    await expect(canceled).rejects.toMatchObject({ name: 'AbortError' });
    expect(gate.waiting).toBe(1);
    gate.close();
    await expect(next).resolves.toBe(false);
    await expect(gate.wait()).resolves.toBe(false);
    const aborted = new AbortController();
    aborted.abort(new Error('why'));
    await expect(gate.wait(aborted.signal)).rejects.toThrow('why');
  });
});

describe('SessionBudget', () => {
  it('reserves all or nothing and serves waiting reservations in order', async () => {
    const budget = new SessionBudget(100);
    expect(budget.tryReserve(60)).toBe(true);
    expect(budget.tryReserve(50)).toBe(false);
    const waiting = budget.reserve(50);
    const controller = new AbortController();
    const canceled = budget.reserve(10, controller.signal);
    controller.abort();
    await expect(canceled).resolves.toBe(false);
    budget.release(20);
    await expect(waiting).resolves.toBe(true);
    expect(budget.reserved).toBe(90);
    await expect(budget.reserve(1000)).resolves.toBe(false);
    const closed = budget.reserve(50);
    budget.close();
    await expect(closed).resolves.toBe(false);
  });
});

describe('LargeMessageReceiver', () => {
  function make(budgetLimit = 1_000_000) {
    /** @type {number[][]} */
    const aborts = [];
    const calls = { credits: 3, returned: 0, aborts };
    const budget = new SessionBudget(budgetLimit);
    const receiver = new LargeMessageReceiver({
      isCallOutstanding: (id) => id === 7,
      tryTakeInvocationCredit: () => --calls.credits >= 0,
      returnInvocationCredit: () => { calls.returned++; calls.credits++; },
      sendAbort: (id, kind, reason) => calls.aborts.push([id, kind, reason]),
    }, budget, 100_000, () => 0);
    return { receiver, budget, calls };
  }
  const arg = (/** @type {number} */ total, id = 1) => ({ invocationId: id, kind: LargeMessageKind.Argument, innerType: 110, totalLength: total });

  it('reassembles chunks of any size and hands the reservation over', () => {
    const { receiver, budget } = make();
    expect(receiver.onStart(arg(5))).toBe(0);
    expect(budget.reserved).toBe(5);
    expect(receiver.onChunk(1, 0, Uint8Array.of(1, 2))).toEqual({ reason: 0, completed: null });
    const done = receiver.onChunk(1, 0, Uint8Array.of(3, 4, 5));
    expect(done).toEqual({ reason: 0, completed: { innerType: 110, body: Uint8Array.of(1, 2, 3, 4, 5), reserved: 5 } });
    expect(receiver.count).toBe(0);
    expect(budget.reserved).toBe(5);
  });

  it('protocol errors (§6.4)', () => {
    const { receiver } = make();
    expect(receiver.onStart(arg(0))).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onStart(arg(100_001))).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onStart({ ...arg(5), innerType: 112 })).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onStart({ ...arg(5), kind: 2 })).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onStart({ invocationId: 8, kind: 1, innerType: 112, totalLength: 5 })).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onStart({ invocationId: 7, kind: 1, innerType: 112, totalLength: 5 })).toBe(0);
    expect(receiver.onStart({ invocationId: 7, kind: 1, innerType: 112, totalLength: 5 })).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onChunk(7, 1, new Uint8Array(6)).reason).toBe(DisconnectReason.ProtocolError);
    expect(receiver.onChunk(9, 1, new Uint8Array(1)).reason).toBe(DisconnectReason.ProtocolError);
  });

  it('an argument without credit is a flow-control error', () => {
    const { receiver } = make();
    for (let i = 1; i <= 3; i++) expect(receiver.onStart(arg(5, i))).toBe(0);
    expect(receiver.onStart(arg(5, 4))).toBe(DisconnectReason.FlowControl);
  });

  it('no budget: refuses with Busy, returns the credit and discards the chunks up to the total', () => {
    const { receiver, calls, budget } = make(10);
    expect(receiver.onStart(arg(20))).toBe(0);
    expect(calls.aborts).toEqual([[1, 0, AbortReason.Busy]]);
    expect(calls.returned).toBe(1);
    expect(budget.reserved).toBe(0);
    expect(receiver.onChunk(1, 0, new Uint8Array(15))).toEqual({ reason: 0, completed: null });
    expect(receiver.onChunk(1, 0, new Uint8Array(5))).toEqual({ reason: 0, completed: null });
    expect(receiver.count).toBe(0);
  });

  it('aborts from the sender: Cancelled ends the stream and returns credit; Ack only ends a refused one; unknown ignored', () => {
    const { receiver, calls, budget } = make();
    receiver.onStart(arg(10));
    receiver.onAbort({ invocationId: 1, kind: 0, reason: AbortReason.Ack });
    expect(receiver.count).toBe(1);
    receiver.onAbort({ invocationId: 1, kind: 0, reason: AbortReason.Cancelled });
    expect(receiver.count).toBe(0);
    expect(calls.returned).toBe(1);
    expect(budget.reserved).toBe(0);
    receiver.onAbort({ invocationId: 99, kind: 0, reason: AbortReason.Cancelled });
  });

  it('reports a stalled stream and clears everything', () => {
    let now = 0;
    const budget = new SessionBudget(100);
    const receiver = new LargeMessageReceiver({
      isCallOutstanding: () => true, tryTakeInvocationCredit: () => true, returnInvocationCredit: () => {}, sendAbort: () => {},
    }, budget, 100, () => now);
    receiver.onStart({ invocationId: 3, kind: 1, innerType: 112, totalLength: 10 });
    now = 500;
    expect(receiver.findStalled(now, 1000)).toBeNull();
    expect(receiver.findStalled(now, 100)).toMatch(/inbound large result of invocation 3, 0 of 10 bytes/);
    receiver.clear();
    expect(receiver.count).toBe(0);
    expect(budget.reserved).toBe(0);
  });
});
