import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ConnectionState } from '../../src/contract/enums.js';
import { NexusBusyError, NexusError } from '../../src/contract/errors.js';
import { counters, getClientSnapshot, isIdle } from '../../src/diagnostics/index.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationManager } from '../../src/invocation/InvocationManager.js';
import { AbortReason, LargeMessageKind, encodeInvocation } from '../../src/invocation/messages.js';
import { NexusMetrics } from '../../src/metrics/index.js';
import { NexusDuplexChannel } from '../../src/pipes/channels.js';
import { PipeState } from '../../src/pipes/constants.js';
import { MessageType } from '../../src/session/protocol.js';
import { connectedHarness as connect, tick, until } from '../invocation/support/harness.js';
import { pack, stringFormatter, testCodec } from '../invocation/support/testCodec.js';
import { ServerPeer, frameItems, pipeArgs, proxyOf } from '../pipes/support/peer.js';
import { connected, createHarness } from '../session/support/harness.js';

/**
 * J17: the snapshot fields and metrics hooks of the session, invocation and pipe layers.
 */

const golden = JSON.parse(readFileSync(fileURLToPath(new URL('../../../../../../tests/golden/snapshot-fields.json', import.meta.url)), 'utf8'));

/** Fields that are nullable in C# and may be null on a connected client. @type {Record<string, string[]>} */
const NULLABLE = {
  NexusSessionSnapshot: ['endpoint', 'sinceLastSent', 'flushInProgress'],
  NexusBudgetSnapshot: [],
  NexusInvocationSnapshot: ['oldestQueuedWait', 'separatePoolOldestQueuedWait'],
  NexusPipeSnapshot: ['closing', 'sinceLastData', 'partialItem', 'waitingForCredit'],
  NexusPartialItemSnapshot: [],
  NexusLargeMessageSnapshot: [],
};

/**
 * Checks that `value` has exactly the golden fields of `record`, and that none C# always fills is null.
 * @param {string} record
 * @param {Record<string, unknown>} value
 */
function expectFilled(record, value) {
  expect(Object.keys(value), record).toEqual(golden.records[record]);
  for (const field of golden.records[record]) {
    if (NULLABLE[record].includes(field)) continue;
    expect(value[field], `${record}.${field}`).not.toBeNull();
    expect(value[field], `${record}.${field}`).not.toBeUndefined();
  }
}

/** @typedef {Awaited<ReturnType<typeof connect>>} Harness */

const KiB = 1024;

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
/** @type {Array<{ client: { disconnect(): Promise<void> }, clock: { advance(ms: number): void } }>} */
let clients = [];
/** @type {NexusMetrics[]} */
let meters = [];

beforeEach(() => {
  previousCodec = setInvocationCodec(testCodec);
});
afterEach(async () => {
  for (const m of meters) m.dispose();
  meters = [];
  for (const h of clients) {
    const done = h.client.disconnect();
    h.clock.advance(60_000);
    await done;
  }
  clients = [];
  await tick();
  setInvocationCodec(previousCodec);
  expect(isIdle(), JSON.stringify(counters())).toBe(true);
});

/** @param {Parameters<typeof connect>[0]} [options] */
async function connectedHarness(options) {
  const h = await connect(options);
  clients.push(h);
  return h;
}

/** A metrics instance whose pushed measurements are recorded. */
function meter() {
  const m = new NexusMetrics();
  meters.push(m);
  /** @type {Array<[string, number, Record<string, string>]>} */
  const pushed = [];
  m.addListener({
    add: (i, v, t) => pushed.push([i.name, v, { ...t }]),
    record: (i, v, t) => pushed.push([i.name, v, { ...t }]),
  });
  /** Observable values by instrument name and the tags beyond role and transport. */
  const collect = () => {
    /** @type {Record<string, number>} */
    const out = {};
    m.collect((i, v, t) => {
      const extra = Object.entries(t).filter(([k]) => k !== 'nexnet.role' && k !== 'nexnet.transport').map(([, value]) => value);
      out[[i.name, ...extra].join('/')] = v;
    });
    return out;
  };
  /** Pushed measurements of one instrument, without the role and transport tags. @param {string} name */
  const of = (name) => pushed.filter(([n]) => n === name).map(([, v, t]) => /** @type {[number, Record<string, string>]} */ ([
    v,
    Object.fromEntries(Object.entries(t).filter(([k]) => k !== 'nexnet.role' && k !== 'nexnet.transport')),
  ]));
  return { m, pushed, collect, of };
}

/** A promise and its resolver. */
function deferred() {
  /** @type {(value?: unknown) => void} */
  let resolve = () => {};
  /** @type {Promise<unknown>} */
  const promise = new Promise((r) => { resolve = r; });
  return { promise, resolve };
}

/**
 * Opens a pipe from the client: createPipe, the pipe method, the server's Ready with `window`.
 * @param {Harness} h
 * @param {ServerPeer} peer
 * @param {{ serverId?: number, window?: number }} [options]
 */
async function openPipe(h, peer, { serverId = 9, window = 64 * KiB } = {}) {
  const pipe = h.client.createPipe();
  await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
  peer.poll();
  const sim = peer.accept(pipe.id, serverId, window);
  await pipe.ready;
  await tick();
  peer.poll();
  return { pipe, sim, id: pipe.id | (serverId << 8) };
}

/** Invocation arguments `[bin(n)]`. @param {number} n */
const bigArgs = (n) => (/** @type {import('../../src/contract/codec.js').MsgPackWriter} */ w) => {
  w.writeArrayHeader(1);
  w.writeBin(new Uint8Array(n));
};

describe('session.snapshot()', () => {
  it('reports the time since the transport connected, since the last write, and the write in flight', async () => {
    const h = createHarness({ transport: { holdWrites: true } });
    clients.push(h);
    h.clock.advance(100);
    await connected(h);
    h.clock.advance(40);
    let snapshot = /** @type {import('../../src/diagnostics/snapshot.js').NexusSessionSnapshot} */ (getClientSnapshot(h.client));
    // The greeting's write is still held by the transport.
    expect(snapshot).toMatchObject({ connected: 40, sinceLastSent: null, flushInProgress: 40 });

    h.transport.holdWrites = false;
    h.transport.releaseWrites();
    await tick();
    h.clock.advance(15);
    snapshot = /** @type {typeof snapshot} */ (getClientSnapshot(h.client));
    expect(snapshot).toMatchObject({ connected: 55, sinceLastSent: 15, flushInProgress: null });
  });
});

describe('client snapshot', () => {
  it('fills every field C# fills: invocations, pipes, partial items, credit waits and large messages', async () => {
    const h = await connectedHarness({
      config: { maxConcurrentConnectionInvocations: 1, maxSeparatePoolInvocations: 1 },
      greeting: { invocationCredits: 2 },
    });
    const { collect } = meter();
    meters[0].track(h.client);
    const peer = new ServerPeer(h);
    const gate = deferred();
    h.nexus.methods.set(0, () => gate.promise);
    h.nexus.methods.set(1, () => gate.promise);
    h.nexus.separate.add(1);

    // A pipe (one outbound credit) with part of a channel item buffered and a writer waiting for credit.
    const { pipe, sim, id } = await openPipe(h, peer, { window: 4 * KiB });
    const reader = await new NexusDuplexChannel(pipe, stringFormatter).getReader();
    const item = frameItems([pack((w) => w.writeString('x'.repeat(100)))]);
    sim.send(item.subarray(0, 40));
    const read = reader.read();
    const writer = pipe.writable.getWriter();
    const write = writer.write(new Uint8Array(6 * KiB));
    write.catch(() => {});

    // One call waiting for its result (the last outbound credit) and one waiting for credit.
    const awaiting = h.proxy.invoke(0, null);
    const waiting = h.proxy.invoke(0, null);
    awaiting.catch(() => {});
    waiting.catch(() => {});

    // Server-to-client: one running and one queued per pool, and a large argument half received.
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 0, 0, null);
    h.sendInvocation(10, 1, 0, null);
    h.sendInvocation(11, 1, 0, null);
    const body = encodeInvocation(30, 0, 0, pack(/** @type {any} */ (bigArgs(100_000))));
    h.sendStart({ invocationId: 30, kind: LargeMessageKind.Argument, innerType: MessageType.Invocation, totalLength: body.length });
    h.sendChunk(30, LargeMessageKind.Argument, body.subarray(0, 50_000));
    await tick();
    h.clock.advance(250);

    const snapshot = /** @type {import('../../src/diagnostics/snapshot.js').NexusSessionSnapshot} */ (getClientSnapshot(h.client));
    expectFilled('NexusSessionSnapshot', /** @type {any} */ (snapshot));
    expectFilled('NexusBudgetSnapshot', /** @type {any} */ (snapshot.budget));
    expectFilled('NexusInvocationSnapshot', /** @type {any} */ (snapshot.invocations));
    expect(snapshot.pipes).toHaveLength(1);
    expectFilled('NexusPipeSnapshot', /** @type {any} */ (snapshot.pipes[0]));
    expectFilled('NexusPartialItemSnapshot', /** @type {any} */ (snapshot.pipes[0].partialItem));
    expect(snapshot.largeMessages).toHaveLength(1);
    expectFilled('NexusLargeMessageSnapshot', /** @type {any} */ (snapshot.largeMessages[0]));

    expect(snapshot).toMatchObject({ state: ConnectionState.Connected, connected: 250, sinceLastSent: 250, flushInProgress: null });
    expect(snapshot.invocations).toEqual({
      queued: 1,
      executing: 1,
      maxExecuting: 1,
      // 32 advertised; 2 queued and the large argument hold one each.
      inboundCredits: 29,
      outboundCredits: 0,
      creditWaits: 1,
      awaitingResults: 1,
      creditDrops: 0,
      oldestQueuedWait: 250,
      separatePoolQueued: 1,
      separatePoolExecuting: 1,
      separatePoolMaxExecuting: 1,
      separatePoolOldestQueuedWait: 250,
    });
    expect(snapshot.pipes[0]).toEqual({
      id,
      isReady: true,
      readerCompleted: false,
      writerCompleted: false,
      closing: null,
      window: 256 * KiB,
      receiveCredit: 256 * KiB - 40,
      buffered: 40,
      consumedNotGranted: 0,
      extraOutstanding: 0,
      sinceLastData: 250,
      partialItem: { length: item.length, received: 40 },
      sendCredit: 0,
      waitingForCredit: 250,
      waitingBytes: 2 * KiB,
    });
    expect(snapshot.largeMessages[0]).toEqual({
      direction: 'inbound',
      kind: 'argument',
      invocationId: 30,
      length: body.length,
      transferred: 50_000,
      sinceLastProgress: 250,
      aborting: false,
    });

    const live = collect();
    expect(live).toMatchObject({
      'nexnet.pipe.open': 1,
      'nexnet.pipe.window': 256 * KiB,
      'nexnet.invocation.queued/normal': 1,
      'nexnet.invocation.queued/separate': 1,
      'nexnet.invocation.executing/normal': 1,
      'nexnet.invocation.executing/separate': 1,
      'nexnet.invocation.credit_waits': 1,
      'nexnet.large_message.in_flight/inbound': 1,
      'nexnet.large_message.in_flight/outbound': 0,
    });

    // The waits end: the writer gets credit, the rest of the item arrives.
    peer.poll();
    sim.consume(4 * KiB);
    sim.send(item.subarray(40));
    await write;
    expect((await read).value).toBe('x'.repeat(100));
    const after = /** @type {typeof snapshot} */ (getClientSnapshot(h.client));
    expect(after.pipes[0]).toMatchObject({ partialItem: null, waitingForCredit: null, waitingBytes: 0 });
    gate.resolve();
    writer.releaseLock();
    await pipe[Symbol.asyncDispose]();
    peer.sendState(id, PipeState.Complete);
  });

  it('shows an outbound large argument while it streams', async () => {
    const h = await connectedHarness();
    h.transport.holdWrites = true;
    const call = h.proxy.invoke(0, bigArgs(100_000));
    call.catch(() => {});
    await tick();
    h.clock.advance(30);
    let large = /** @type {import('../../src/diagnostics/snapshot.js').NexusSessionSnapshot} */ (getClientSnapshot(h.client)).largeMessages;
    expect(large).toHaveLength(1);
    expect(large[0]).toMatchObject({ direction: 'outbound', kind: 'argument', transferred: 0, sinceLastProgress: 30, aborting: false });
    expect(large[0].length).toBeGreaterThan(100_000);

    h.transport.releaseWrites();
    await tick();
    h.transport.releaseWrites();
    await tick();
    large = /** @type {import('../../src/diagnostics/snapshot.js').NexusSessionSnapshot} */ (getClientSnapshot(h.client)).largeMessages;
    expect(large[0].transferred).toBe(0xffff);
    expect(large[0].sinceLastProgress).toBe(0);
    h.transport.holdWrites = false;
    h.transport.releaseWrites();
  });
});

describe('metrics hooks', () => {
  it('counts a session that ends during the handshake, timed from the transport connection', async () => {
    const h = createHarness();
    clients.push(h);
    const { m, pushed, collect } = meter();
    m.track(h.client);
    const result = h.client.tryConnect();
    await tick();
    expect(collect()).toMatchObject({ 'nexnet.session.active': 1, 'nexnet.session.handshaking': 1 });
    h.clock.advance(1200);
    h.transport.serverClose();
    expect((await result).success).toBe(false);
    expect(pushed).toEqual([
      ['nexnet.session.duration', 1.2, { 'nexnet.role': 'client', 'nexnet.transport': 'tcp', 'nexnet.disconnect.reason': 'socket_error' }],
      ['nexnet.session.disconnects', 1, { 'nexnet.role': 'client', 'nexnet.transport': 'tcp', 'nexnet.disconnect.reason': 'socket_error', 'nexnet.session.phase': 'handshake' }],
    ]);
    expect(collect()).toMatchObject({ 'nexnet.session.active': 0, 'nexnet.session.handshaking': 0 });
    expect(counters().subscriptions).toBe(1);
    m.untrack(h.client);
    expect(counters().subscriptions).toBe(0);
  });

  it('a session already open when tracking starts is timed from its transport connection', async () => {
    const h = createHarness();
    clients.push(h);
    await connected(h);
    h.clock.advance(500);
    const { m, of } = meter();
    m.track(h.client);
    h.clock.advance(500);
    await h.client.disconnect();
    expect(of('nexnet.session.duration')).toEqual([[1, { 'nexnet.disconnect.reason': 'graceful' }]]);
    expect(of('nexnet.session.disconnects')).toEqual([[1, { 'nexnet.disconnect.reason': 'graceful', 'nexnet.session.phase': 'established' }]]);
  });

  it('records queue waits, pipe refusals, resets and credit waits', async () => {
    const h = await connectedHarness({ config: { maxConcurrentConnectionInvocations: 1, maxPipes: 3 } });
    const { m, of } = meter();
    m.track(h.client);
    const peer = new ServerPeer(h);

    // Queue wait: the second invocation waits 300 ms for the slot.
    const gate = deferred();
    h.nexus.methods.set(0, () => gate.promise);
    h.sendInvocation(1, 0, 0, null);
    h.sendInvocation(2, 0, 0, null);
    await tick();
    h.clock.advance(300);
    gate.resolve();
    await until(() => of('nexnet.invocation.queue_wait').length === 2);
    expect(of('nexnet.invocation.queue_wait')).toEqual([
      [0, { 'nexnet.invocation.pool': 'normal' }],
      [0.3, { 'nexnet.invocation.pool': 'normal' }],
    ]);

    // Credit wait: a write of twice the server's window waits 75 ms for a grant.
    const { pipe, sim, id } = await openPipe(h, peer, { window: KiB });
    const writer = pipe.writable.getWriter();
    const write = writer.write(new Uint8Array(2 * KiB));
    await tick();
    h.clock.advance(75);
    peer.poll();
    sim.consume(KiB);
    await write;
    expect(of('nexnet.pipe.credit_wait')).toEqual([[0.075, {}]]);

    // Reset: a channel item that does not decode.
    const reader = await new NexusDuplexChannel(pipe, stringFormatter).getReader();
    sim.send(frameItems([pack((w) => w.writeInt(5))]));
    await expect(reader.read()).rejects.toBeInstanceOf(NexusError);
    expect(of('nexnet.pipe.resets')).toEqual([[1, { 'nexnet.reset.reason': 'invalid_item' }]]);
    writer.releaseLock();
    await pipe[Symbol.asyncDispose]();
    peer.sendState(id, PipeState.Complete);
    await tick();

    // Refusals: maxPipes on this side, and the server refusing an open.
    const a = h.client.createPipe();
    const b = h.client.createPipe();
    const c = h.client.createPipe();
    expect(() => h.client.createPipe()).toThrow(NexusBusyError);
    await proxyOf(h)._invokePipe(5, a, pipeArgs);
    peer.sendState(a.id, PipeState.Refused);
    await expect(a.ready).rejects.toBeInstanceOf(NexusBusyError);
    expect(of('nexnet.pipe.refusals')).toEqual([
      [1, { 'nexnet.refusal.side': 'local' }],
      [1, { 'nexnet.refusal.side': 'remote' }],
    ]);
    await b[Symbol.asyncDispose]();
    await c[Symbol.asyncDispose]();
  });

  it('records large-message aborts in both directions', async () => {
    const h = await connectedHarness({ config: { sessionBudget: 20 * 1024 * 1024 } });
    const { m, of } = meter();
    m.track(h.client);
    const body = encodeInvocation(9, 0, 0, pack(/** @type {any} */ (bigArgs(100_000))));

    // Inbound, cancelled by the sender.
    h.sendStart({ invocationId: 9, kind: LargeMessageKind.Argument, innerType: MessageType.Invocation, totalLength: body.length });
    h.sendAbort(9, LargeMessageKind.Argument, AbortReason.Cancelled);
    // Inbound, refused for lack of budget.
    const budget = /** @type {import('../../src/invocation/InvocationManager.js').InvocationManager} */ (getInvocationManager(h.session)).budget;
    const filler = budget.limit - budget.reserved - 10;
    expect(budget.tryReserve(filler)).toBe(true);
    h.sendStart({ invocationId: 10, kind: LargeMessageKind.Argument, innerType: MessageType.Invocation, totalLength: body.length });
    h.sendChunk(10, LargeMessageKind.Argument, body.subarray(0, 60_000));
    h.sendChunk(10, LargeMessageKind.Argument, body.subarray(60_000));
    budget.release(filler);

    // Outbound, refused by the server.
    const refused = h.proxy.invoke(0, bigArgs(100_000));
    refused.catch(() => {});
    await tick();
    h.sendAbort(1, LargeMessageKind.Argument, AbortReason.TooLarge);
    await expect(refused).rejects.toBeInstanceOf(NexusError);

    // Outbound, cancelled by the caller while it streams.
    h.transport.holdWrites = true;
    const controller = new AbortController();
    const canceled = h.proxy.invoke(0, bigArgs(100_000), { signal: controller.signal });
    canceled.catch(() => {});
    await tick();
    controller.abort();
    h.transport.holdWrites = false;
    h.transport.releaseWrites();
    await expect(canceled).rejects.toThrow();
    await tick();

    expect(of('nexnet.large_message.aborts')).toEqual([
      [1, { 'nexnet.direction': 'inbound', 'nexnet.abort.reason': 'cancelled' }],
      [1, { 'nexnet.direction': 'inbound', 'nexnet.abort.reason': 'busy' }],
      [1, { 'nexnet.direction': 'outbound', 'nexnet.abort.reason': 'too_large' }],
      [1, { 'nexnet.direction': 'outbound', 'nexnet.abort.reason': 'cancelled' }],
    ]);
    expect(h.session.isDisconnecting).toBe(false);
  });

  it('stops reporting a client once untracked, and a new session of a tracked client is reported', async () => {
    const h = createHarness();
    clients.push(h);
    const { m, of } = meter();
    m.track(h.client);
    await connected(h);
    h.transport.serverClose();
    await h.client.disconnected;
    await connected(h);
    h.clock.advance(2000);
    h.transport.serverClose();
    await h.client.disconnected;
    expect(of('nexnet.session.disconnects').map(([, t]) => t['nexnet.disconnect.reason'])).toEqual(['socket_error', 'socket_error']);
    expect(of('nexnet.session.duration').map(([v]) => v)).toEqual([0, 2]);
    m.untrack(h.client);
    await connected(h);
    h.transport.serverClose();
    await h.client.disconnected;
    expect(of('nexnet.session.disconnects')).toHaveLength(2);
  });
});
