import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NexusCollectionState } from '../../src/contract/enums.js';
import { NexusBusyError, NexusError, NexusTransportError, isCancellationError } from '../../src/contract/errors.js';
import { NexusList as ContractNexusList } from '../../src/contract/collections.js';
import { NexusCollectionMode, NexusList, defaultEquals } from '../../src/collections/index.js';
import { ListMessageTag, listMessageFormatter } from '../../src/collections/messages.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { InvocationFlags, decodeInvocation } from '../../src/invocation/messages.js';
import { PipeState } from '../../src/pipes/constants.js';
import { getPipeCounters } from '../../src/pipes/diagnostics.js';
import { encodeFrame, encodePipeFrame } from '../../src/session/frames.js';
import { MessageType } from '../../src/session/protocol.js';
import { encodeDuplexPipeUpdateState } from '../../src/session/wire.js';
import { concat } from '../session/support/LoopbackTransport.js';
import { serverHello } from '../session/support/harness.js';
import { connectedHarness as connect, tick, until } from '../invocation/support/harness.js';
import { ServerPeer, frameItems, proxyOf } from '../pipes/support/peer.js';
import { collectionTestCodec, intFormatter, toHex } from './support/codec.js';

/** @typedef {Awaited<ReturnType<typeof connect>>} Harness */
/** @typedef {import('../../src/collections/messages.js').ListMessage<number>} Item */

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
/** @type {Harness[]} */
let harnesses = [];
beforeEach(() => {
  previousCodec = setInvocationCodec(collectionTestCodec);
});
afterEach(async () => {
  for (const h of harnesses) await h.client.disconnect();
  harnesses = [];
  setInvocationCodec(previousCodec);
});

/** @param {Parameters<typeof connect>[0]} [options] */
async function connectedHarness(options) {
  const h = await connect(options);
  harnesses.push(h);
  return h;
}

const COLLECTION_ID = 7;
const SERVER_ID = 2;
const formatter = listMessageFormatter(intFormatter);
const { BiDirectional, ServerToClient, Relay } = NexusCollectionMode;

/** @param {Item} item @returns {Uint8Array} */
function encodeItem(item) {
  const writer = collectionTestCodec.createWriter();
  formatter.write(writer, item);
  return writer.toUint8Array();
}

/** @param {Uint8Array} bytes @returns {Item} */
function decodeItem(bytes) {
  return formatter.read(collectionTestCodec.createReader(bytes));
}

const start = (/** @type {number} */ version, /** @type {number} */ totalValues) => ({ tag: ListMessageTag.ResetStart, flags: 0, version, totalValues });
const values = (/** @type {number[]} */ v) => ({ tag: ListMessageTag.ResetValues, flags: 0, values: v });
const complete = () => ({ tag: ListMessageTag.ResetComplete, flags: 0 });
const ack = (/** @type {boolean} */ a) => (a ? 1 : 0);
const insert = (/** @type {number} */ version, /** @type {number} */ index, /** @type {number} */ value, a = false) => ({ tag: ListMessageTag.Insert, flags: ack(a), version, index, value });
const replace = (/** @type {number} */ version, /** @type {number} */ index, /** @type {number} */ value, a = false) => ({ tag: ListMessageTag.Replace, flags: ack(a), version, index, value });
const move = (/** @type {number} */ version, /** @type {number} */ fromIndex, /** @type {number} */ toIndex, a = false) => ({ tag: ListMessageTag.Move, flags: ack(a), version, fromIndex, toIndex });
const remove = (/** @type {number} */ version, /** @type {number} */ index, a = false) => ({ tag: ListMessageTag.Remove, flags: ack(a), version, index });
const clear = (/** @type {number} */ version, a = false) => ({ tag: ListMessageTag.Clear, flags: ack(a), version });
const noop = () => ({ tag: ListMessageTag.Noop, flags: 1 });

/**
 * The server side of one collection pipe.
 */
class ServerList {
  /** @param {ServerPeer} peer @param {import('../pipes/support/peer.js').SimPipe} sim @param {number} id */
  constructor(peer, sim, id) {
    this.peer = peer;
    this.sim = sim;
    this.id = id;
  }

  /** Sends items in one write. @param {...Item} items */
  send(...items) {
    this.peer.poll();
    const bytes = frameItems(items.map(encodeItem));
    expect(this.sim.send(bytes)).toBe(bytes.length);
  }

  /** The requests the client sent since the last call. @returns {Item[]} */
  requests() {
    this.peer.poll();
    return this.sim.takeItems().map(decodeItem);
  }

  /** The server closes the pipe. */
  close() {
    this.peer.sendState(this.id, PipeState.Complete);
  }

  /** True once the client sent its Complete for this pipe. */
  clientClosed() {
    return this.peer.states().some((s) => s.pipeId === this.id && s.state === PipeState.Complete);
  }
}

/**
 * The collection invocations the client sent since the handshake.
 * @param {Harness} h
 */
function collectionInvocations(h) {
  return h.framesOf(MessageType.Invocation).map((f) => decodeInvocation(f.body)).filter((i) => i.methodId === COLLECTION_ID);
}

/**
 * Starts an enable and accepts its pipe on the server.
 * @param {Harness} h
 * @param {ServerPeer} peer
 * @param {NexusList<number>} list
 * @returns {Promise<{ server: ServerList, enabling: Promise<boolean> }>}
 */
async function acceptEnable(h, peer, list) {
  const before = collectionInvocations(h).length;
  const enabling = list.enable();
  await until(() => collectionInvocations(h).length > before);
  const invocation = collectionInvocations(h).at(-1);
  if (!invocation?.args) throw new Error('no invocation');
  // [pipeId]
  expect(invocation.args[0]).toBe(0x91);
  const clientId = invocation.args[1];
  const sim = peer.accept(clientId, SERVER_ID, 64 * 1024);
  await tick();
  peer.poll();
  return { server: new ServerList(peer, sim, clientId | (SERVER_ID << 8)), enabling };
}

/**
 * A list enabled and synchronized with `items` at `version`.
 * @param {{ mode?: number, items?: number[], version?: number, options?: object }} [setup]
 */
async function readyList({ mode = BiDirectional, items = [1, 2, 3], version = 3, options = {} } = {}) {
  const h = await connectedHarness();
  const peer = new ServerPeer(h);
  /** @type {NexusList<number>} */
  const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter, { mode, ...options });
  /** @type {any[]} */
  const changes = [];
  list.addEventListener('changed', (e) => changes.push(/** @type {CustomEvent} */ (e).detail));
  const { server, enabling } = await acceptEnable(h, peer, list);
  server.send(start(version, items.length), values(items), complete());
  expect(await enabling).toBe(true);
  changes.length = 0;
  return { h, peer, list, server, changes };
}

/** @param {Harness} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
}

describe('enable', () => {
  it('invokes the collection with [pipeId] as IgnoreReturn | DuplexPipe and resolves once the reset completed', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter, { mode: BiDirectional });
    expect(list).toBeInstanceOf(ContractNexusList);
    expect(ContractNexusList).toBe(NexusList);
    expect(list.state).toBe(NexusCollectionState.Disconnected);
    expect(list.isEnabled).toBe(false);
    await expect(list.ready).resolves.toBeUndefined();
    await expect(list.disabled).resolves.toBeUndefined();
    /** @type {string[]} */
    const actions = [];
    list.addEventListener('changed', (e) => actions.push(/** @type {CustomEvent} */ (e).detail.action));

    const { server, enabling } = await acceptEnable(h, peer, list);
    const [invocation] = collectionInvocations(h);
    expect(invocation.flags).toBe(InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe);
    expect(invocation.invocationId).toBe(0);
    expect(toHex(/** @type {Uint8Array} */ (invocation.args))).toBe(`91${(server.id & 0xff).toString(16).padStart(2, '0')}`);
    expect(list.isEnabled).toBe(true);
    expect(list.enable()).toBe(enabling);

    server.send(start(5, 4), values([10, 20]));
    await tick();
    expect(list.state).toBe(NexusCollectionState.Disconnected);
    expect(list.count).toBe(0);
    server.send(values([30, 40]), complete());
    expect(await enabling).toBe(true);
    await expect(list.ready).resolves.toBeUndefined();
    expect(list.state).toBe(NexusCollectionState.Connected);
    expect(actions).toEqual(['reset', 'ready']);
    expect(list.toArray()).toEqual([10, 20, 30, 40]);
    expect([...list]).toEqual([10, 20, 30, 40]);
    expect([list.count, list.length, list.version]).toEqual([4, 4, 5]);
    expect([list.get(1), list.at(-2), list.at(9)]).toEqual([20, 30, undefined]);
    expect(() => list.get(4)).toThrow(RangeError);
    expect([list.indexOf(30), list.indexOf(40), list.contains(99), list.contains(10)]).toEqual([2, 3, false, true]);
    await expect(list.enable()).resolves.toBe(true);
    expect(collectionInvocations(h)).toHaveLength(1);
    await finish(h);
  });

  it('buffers changes that arrive before ResetStart and replays them with the stale rule (F17)', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter);
    /** @type {any[]} */
    const changes = [];
    list.addEventListener('changed', (e) => changes.push(/** @type {CustomEvent} */ (e).detail));
    const { server, enabling } = await acceptEnable(h, peer, list);
    // Version 2 is already in the snapshot; version 3 is not.
    server.send(insert(2, 0, 7), insert(3, 1, 8), start(2, 1), values([7]), complete());
    expect(await enabling).toBe(true);
    expect(list.toArray()).toEqual([7, 8]);
    expect(list.version).toBe(3);
    expect(changes).toEqual([{ action: 'add', index: 1, item: 8 }, { action: 'reset' }, { action: 'ready' }]);
    await finish(h);
  });

  it('rejects when not connected, and with the abort reason when aborted before sending', async () => {
    const h = await connectedHarness();
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter);
    await expect(list.enable({ signal: AbortSignal.abort() })).rejects.toSatisfy(isCancellationError);
    await h.client.disconnect();
    await expect(list.enable()).rejects.toBeInstanceOf(NexusTransportError);
    expect(list.isEnabled).toBe(false);
    expect(getPipeCounters().openPipes).toBe(0);
  });

  it('a refused pipe rejects with NexusBusyError, fails ready, and can be retried; the session stays up', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter);
    const enabling = list.enable();
    await until(() => collectionInvocations(h).length === 1);
    const ready = list.ready;
    const clientId = /** @type {Uint8Array} */ (collectionInvocations(h)[0].args)[1];
    peer.sendState(clientId, PipeState.Refused);
    await expect(enabling).rejects.toBeInstanceOf(NexusBusyError);
    await expect(ready).rejects.toBeInstanceOf(NexusError);
    expect(list.isEnabled).toBe(false);
    expect(list.state).toBe(NexusCollectionState.Disconnected);
    expect(getPipeCounters().openPipes).toBe(0);
    expect(h.client.state).toBe(2);

    const { server, enabling: retry } = await acceptEnable(h, peer, list);
    server.send(start(1, 0), complete());
    expect(await retry).toBe(true);
    await finish(h);
  });

  it('resolves false and fails ready when the pipe closes before the initial state', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter);
    const { server, enabling } = await acceptEnable(h, peer, list);
    const ready = list.ready;
    server.send(start(1, 2), values([1]));
    server.close();
    expect(await enabling).toBe(false);
    await expect(ready).rejects.toThrow(/before its initial synchronization/);
    expect(list.isEnabled).toBe(false);
    expect(list.count).toBe(0);
    await finish(h);
  });

  it('disable while the pipe opens rejects the enable and frees the pipe', async () => {
    const h = await connectedHarness();
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter);
    const enabling = list.enable();
    await until(() => collectionInvocations(h).length === 1);
    await list.disable();
    await expect(enabling).rejects.toSatisfy(isCancellationError);
    expect(list.isEnabled).toBe(false);
    await finish(h);
  });
});

describe('reads and change events', () => {
  it('applies every change with its event detail and version; drops stale changes; accepts version 0', async () => {
    const { h, list, server, changes } = await readyList({ mode: ServerToClient });
    server.send(
      insert(4, 1, 9), replace(5, 0, 8), move(6, 0, 3), remove(7, 2),
      insert(7, 0, 100), // stale
      clear(8), insert(0, 0, 5), insert(0, 0, 6), // version 0: never stale
    );
    await until(() => changes.length === 7);
    expect(changes).toEqual([
      { action: 'add', index: 1, item: 9 },
      { action: 'replace', index: 0, item: 8, oldItem: 1 },
      { action: 'move', fromIndex: 0, toIndex: 3, item: 8 },
      { action: 'remove', index: 2, item: 3 },
      { action: 'reset' },
      { action: 'add', index: 0, item: 5 },
      { action: 'add', index: 0, item: 6 },
    ]);
    expect(list.toArray()).toEqual([6, 5]);
    expect(list.version).toBe(0);
    await finish(h);
  });

  it('an iterator keeps the snapshot it started with', async () => {
    const { h, list, server, changes } = await readyList();
    const iterator = list[Symbol.iterator]();
    const array = list.toArray();
    server.send(clear(4));
    await until(() => changes.length === 1);
    expect([...{ [Symbol.iterator]: () => iterator }]).toEqual([1, 2, 3]);
    expect(array).toEqual([1, 2, 3]);
    expect(list.count).toBe(0);
    await finish(h);
  });
});

describe('mutations', () => {
  it('a mutation made during enable waits for the initial state, then goes out with its base version', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter, { mode: BiDirectional });
    const { server, enabling } = await acceptEnable(h, peer, list);
    const adding = list.add(4);
    const removing = list.removeAt(0);
    server.send(start(6, 2), values([1]));
    await tick();
    expect(server.requests()).toEqual([]);
    server.send(values([2]), complete());
    expect(await enabling).toBe(true);
    /** @type {Item[]} */
    const sent = [];
    await until(() => sent.push(...server.requests()) > 0);
    expect(sent).toEqual([insert(6, 2, 4)]);
    server.send(insert(7, 2, 4, true));
    expect(await adding).toBe(true);
    await tick();
    expect(server.requests()).toEqual([remove(7, 0)]);
    server.send(remove(8, 0, true));
    expect(await removing).toBe(true);
    expect(list.toArray()).toEqual([2, 4]);
    await finish(h);
  });

  it('a mutation made during enable resolves false when the enable fails; nothing is sent', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(COLLECTION_ID, intFormatter, { mode: BiDirectional });
    const enabling = list.enable();
    const adding = list.add(4);
    await until(() => collectionInvocations(h).length === 1);
    const clientId = /** @type {Uint8Array} */ (collectionInvocations(h)[0].args)[1];
    peer.sendState(clientId, PipeState.Refused);
    await expect(enabling).rejects.toBeInstanceOf(NexusBusyError);
    expect(await adding).toBe(false);

    // The pipe opens but closes before the initial state.
    const { server, enabling: retry } = await acceptEnable(h, peer, list);
    const clearing = list.clear();
    server.send(start(1, 1));
    server.close();
    expect(await retry).toBe(false);
    expect(await clearing).toBe(false);
    expect(server.requests()).toEqual([]);
    await finish(h);
  });

  it('send requests with the base version and resolve when the Ack change is applied; nothing is applied locally', async () => {
    const { h, list, server, changes } = await readyList();
    const adding = list.add(4);
    await tick();
    expect(server.requests()).toEqual([insert(3, 3, 4)]);
    expect(list.count).toBe(3);
    server.send(insert(4, 3, 4, true));
    expect(await adding).toBe(true);
    expect(list.toArray()).toEqual([1, 2, 3, 4]);
    expect(changes).toEqual([{ action: 'add', index: 3, item: 4 }]);

    const cases = /** @type {Array<[() => Promise<boolean>, Item, Item]>} */ ([
      [() => list.insert(0, 9), insert(4, 0, 9), insert(5, 0, 9, true)],
      [() => list.replace(1, 7), replace(5, 1, 7), replace(6, 1, 7, true)],
      [() => list.move(0, 2), move(6, 0, 2), move(7, 0, 2, true)],
      [() => list.removeAt(1), remove(7, 1), remove(8, 1, true)],
      [() => list.remove(4), remove(8, 3), remove(9, 3, true)],
      [() => list.clear(), clear(9), clear(10, true)],
    ]);
    for (const [call, request, answer] of cases) {
      const pending = call();
      await tick();
      expect(server.requests()).toEqual([request]);
      server.send(answer);
      expect(await pending).toBe(true);
    }
    expect(list.count).toBe(0);
    expect(list.version).toBe(10);
    await finish(h);
  });

  it('one request at a time: the next is built and sent once the previous was answered', async () => {
    const { h, list, server } = await readyList();
    const first = list.add(4);
    const second = list.add(5);
    const third = list.remove(1);
    await tick();
    expect(server.requests()).toEqual([insert(3, 3, 4)]);
    // Another session's change arrives first, then the answer.
    server.send(insert(4, 0, 0), insert(5, 4, 4, true));
    expect(await first).toBe(true);
    await tick();
    expect(server.requests()).toEqual([insert(5, 5, 5)]);
    // The winning change, then the Noop for this request (F19).
    server.send(remove(6, 0), noop());
    expect(await second).toBe(false);
    await tick();
    expect(server.requests()).toEqual([remove(6, 0)]);
    server.send(remove(7, 0, true));
    expect(await third).toBe(true);
    expect(list.toArray()).toEqual([2, 3, 4]);
    await finish(h);
  });

  it('remove(item) finds the item with the list equality and sends nothing when no item is equal', async () => {
    const { h, list, server } = await readyList({ items: [1, 22, 3], options: { equals: (/** @type {number} */ a, /** @type {number} */ b) => a % 10 === b % 10 } });
    await expect(list.remove(45)).resolves.toBe(false);
    const removing = list.remove(12);
    await tick();
    expect(server.requests()).toEqual([remove(3, 1)]);
    server.send(remove(4, 1, true));
    expect(await removing).toBe(true);
    await finish(h);
  });

  it('custom equality and value types with equals()', async () => {
    const h = await connectedHarness();
    /** @type {NexusList<number>} */
    const list = proxyOf(h)._collection(9, intFormatter, { equals: (/** @type {number} */ a, /** @type {number} */ b) => a % 10 === b % 10 });
    expect(list.indexOf(1)).toBe(-1);
    const point = { x: 1, equals: (/** @type {any} */ o) => o?.x === 1 };
    expect(defaultEquals(point, { x: 1 })).toBe(true);
    expect(defaultEquals({ x: 1 }, { x: 1 })).toBe(false);
    expect(defaultEquals(0, -0)).toBe(true);
    expect(defaultEquals(Number.NaN, Number.NaN)).toBe(true);
    expect(defaultEquals(1n, 1n)).toBe(true);
    expect(defaultEquals(null, undefined)).toBe(false);
    await finish(h);
  });

  it('reject in ServerToClient and Relay mode, before ready, and for negative indexes; nothing is sent', async () => {
    for (const mode of [ServerToClient, Relay]) {
      const { h, list, server } = await readyList({ mode });
      await expect(list.add(1)).rejects.toThrow(/read-only/);
      await expect(list.clear()).rejects.toBeInstanceOf(NexusError);
      expect(server.requests()).toEqual([]);
      await finish(h);
    }
    const { h, list, server } = await readyList();
    for (const call of [() => list.insert(-1, 1), () => list.removeAt(-1), () => list.replace(1.5, 1), () => list.move(0, -2)]) {
      await expect(call()).rejects.toBeInstanceOf(RangeError);
    }
    await list.disable();
    await expect(list.add(1)).rejects.toThrow(/not enabled/);
    expect(server.requests()).toEqual([]);

    /** @type {NexusList<number>} */
    const fresh = proxyOf(h)._collection(11, intFormatter, { mode: BiDirectional });
    await expect(fresh.add(1)).rejects.toThrow(/not enabled/);
    expect(proxyOf(h)._collection(12, intFormatter).mode).toBe(ServerToClient);
    expect(() => proxyOf(h)._collection(13, intFormatter, { mode: 9 })).toThrow(TypeError);
    await finish(h);
  });
});

describe('closing', () => {
  it('disable closes the pipe, empties the list, raises reset and resolves disabled; enable again resynchronizes', async () => {
    const { h, peer, list, server, changes } = await readyList();
    const disabled = list.disabled;
    await list.disable();
    await disabled;
    expect(server.clientClosed()).toBe(true);
    expect(list.isEnabled).toBe(false);
    expect(list.state).toBe(NexusCollectionState.Disconnected);
    expect([list.count, list.version]).toEqual([0, 0]);
    expect(changes).toEqual([{ action: 'reset' }]);
    // The ready outcome of the last enable is kept.
    await expect(list.ready).resolves.toBeUndefined();
    server.close();
    await tick();

    const { server: next, enabling } = await acceptEnable(h, peer, list);
    expect(next.id).not.toBe(server.id);
    next.send(start(9, 1), values([5]), complete());
    expect(await enabling).toBe(true);
    expect(list.toArray()).toEqual([5]);
    await finish(h);
  });

  it('the server closing the pipe completes the pending request with false', async () => {
    const { h, list, server, changes } = await readyList();
    const adding = list.add(4);
    await tick();
    server.close();
    expect(await adding).toBe(false);
    expect(list.isEnabled).toBe(false);
    expect(changes).toEqual([{ action: 'reset' }]);
    await finish(h);
  });

  it('a protocol error closes the pipe (Ack change that fails validation answers false); the session stays up', async () => {
    const { h, list, server, changes } = await readyList();
    const adding = list.add(4);
    await tick();
    server.send(insert(4, 9, 4, true));
    expect(await adding).toBe(false);
    await until(() => !list.isEnabled);
    expect(server.clientClosed()).toBe(true);
    expect(changes).toEqual([{ action: 'reset' }]);
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('an item that does not decode closes the pipe', async () => {
    const { h, peer, list, server } = await readyList();
    peer.poll();
    const garbage = frameItems([Uint8Array.of(0x92, 0x0c, 0x91, 0x00)]);
    expect(server.sim.send(garbage)).toBe(garbage.length);
    await until(() => !list.isEnabled);
    expect(server.clientClosed()).toBe(true);
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('items still buffered in a closed pipe never reach the list or the next enable (F19)', async () => {
    const { h, peer, list, server, changes } = await readyList({ items: [1], version: 1 });
    peer.poll();
    // A change and the server's close arrive in one read.
    const item = frameItems([encodeItem(insert(2, 0, 99))]);
    server.sim.sendCredit -= item.length;
    peer.send(concat([
      encodePipeFrame(MessageType.DuplexPipeWrite, server.id, item),
      encodeFrame(MessageType.DuplexPipeUpdateState, encodeDuplexPipeUpdateState(server.id, PipeState.Complete, 0)),
    ]));
    await until(() => !list.isEnabled);
    expect(changes).toEqual([{ action: 'reset' }]);

    const { server: next, enabling } = await acceptEnable(h, peer, list);
    next.send(start(5, 1), values([7]), complete());
    expect(await enabling).toBe(true);
    expect(list.toArray()).toEqual([7]);
    expect(changes.map((c) => c.action)).toEqual(['reset', 'reset', 'ready']);
    await finish(h);
  });

  it('a session end disables the list; it is not resumed after a reconnect, and enable opens a new pipe', async () => {
    const { h, peer, list, changes } = await readyList();
    const adding = list.add(4);
    const disabled = list.disabled;
    await tick();
    h.transport.serverClose();
    expect(await adding).toBe(false);
    await disabled;
    expect(list.isEnabled).toBe(false);
    expect(list.count).toBe(0);
    expect(changes).toEqual([{ action: 'reset' }]);

    const reconnect = h.client.tryConnect();
    await tick();
    h.transport.serverSend(serverHello());
    expect((await reconnect).success).toBe(true);
    await tick();
    expect(list.isEnabled).toBe(false);
    expect(collectionInvocations(h)).toHaveLength(0);

    const freshPeer = new ServerPeer(h);
    const { server, enabling } = await acceptEnable(h, freshPeer, list);
    server.send(start(2, 2), values([8, 9]), complete());
    expect(await enabling).toBe(true);
    expect(list.toArray()).toEqual([8, 9]);
    void peer;
    await finish(h);
  });
});
