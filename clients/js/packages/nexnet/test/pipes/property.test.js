/**
 * Property tests of pipe flow control: random windows, chunk sizes, read sizes and item sizes. Every case must finish
 * (no stall), preserve the byte stream (and the item sequence) in both directions, and never exceed the credit.
 * A failing case names its seed; replay it with `runRaw(seed)` or `runChannel(seed)`.
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationManager } from '../../src/invocation/InvocationManager.js';
import { NexusDuplexChannel } from '../../src/pipes/channels.js';
import { PipeState } from '../../src/pipes/constants.js';
import { getPipeCounters } from '../../src/pipes/diagnostics.js';
import { readLength } from '../../src/pipes/framing.js';
import { connectedHarness as connect, tick } from '../invocation/support/harness.js';
import { binFormatter, pack, testCodec } from '../invocation/support/testCodec.js';
import { concat } from '../session/support/LoopbackTransport.js';
import { ServerPeer, bytesOf, frameItems, pipeArgs, proxyOf, random } from './support/peer.js';

/** @typedef {Awaited<ReturnType<typeof connect>>} Harness */

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
/** @type {Harness[]} */
let harnesses = [];
beforeEach(() => {
  previousCodec = setInvocationCodec(testCodec);
});
afterEach(async () => {
  for (const h of harnesses) await h.client.disconnect();
  harnesses = [];
  setInvocationCodec(previousCodec);
});

/** Lets every pending task run (faster than a timer tick). */
const turn = () => new Promise((resolve) => setImmediate(resolve));

const MAX_STEPS = 20_000;
const serverWriterDone = PipeState.Ready | PipeState.ClientReaderServerWriterComplete;

/**
 * @param {number} seed
 * @param {number} clientWindow
 * @param {number} serverWindow
 */
async function open(seed, clientWindow, serverWindow) {
  const h = await connect({ config: { pipeWindow: clientWindow } });
  harnesses.push(h);
  const peer = new ServerPeer(h);
  const pipe = h.client.createPipe();
  await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
  const serverId = 1 + (seed % 200);
  const sim = peer.accept(pipe.id, serverId, serverWindow);
  await pipe.ready;
  const budget = /** @type {import('../../src/invocation/InvocationManager.js').InvocationManager} */ (getInvocationManager(h.session)).budget;
  return { h, peer, pipe, sim, id: pipe.id | (serverId << 8), budget };
}

/**
 * @param {number} seed
 * @param {string} what
 * @param {Record<string, unknown>} state
 * @returns {Error}
 */
function stalled(seed, what, state) {
  return new Error(`seed ${seed}: ${what} stalled: ${JSON.stringify(state)}`);
}

/**
 * Raw bytes both ways: the server writes in random frames within its credit and consumes at random; the client reads
 * with random BYOB sizes or whole chunks and writes random chunks.
 * @param {number} seed
 */
async function runRaw(seed) {
  const rng = random(seed);
  const clientWindow = rng.int(4096, 80_000);
  const serverWindow = rng.int(1024, 120_000);
  const toClient = bytesOf(rng, rng.int(0, 120_000));
  const toServer = bytesOf(rng, rng.int(0, 120_000));
  const { h, peer, pipe, sim, id, budget } = await open(seed, clientWindow, serverWindow);
  const baseline = budget.reserved;

  const readRng = random(seed * 7 + 1);
  const reading = (async () => {
    /** @type {Uint8Array[]} */
    const chunks = [];
    if (readRng.next() < 0.5) {
      const reader = pipe.readable.getReader({ mode: 'byob' });
      for (;;) {
        const { value, done } = await reader.read(new Uint8Array(readRng.int(1, 30_000)));
        if (done) break;
        chunks.push(/** @type {Uint8Array} */ (value));
      }
    } else {
      for await (const chunk of pipe.readable) chunks.push(chunk);
    }
    return concat(chunks);
  })();

  const writeRng = random(seed * 7 + 2);
  const writing = (async () => {
    const writer = pipe.writable.getWriter();
    for (let offset = 0; offset < toServer.length;) {
      const n = writeRng.int(1, 70_000);
      await writer.write(toServer.subarray(offset, offset + n));
      offset += n;
    }
    await writer.close();
  })();

  let readDone = false;
  let writeDone = false;
  void reading.then(() => { readDone = true; });
  void writing.then(() => { writeDone = true; });
  let sent = 0;
  let completed = false;
  for (let step = 0; ; step++) {
    if (step > MAX_STEPS) {
      throw stalled(seed, 'raw', { clientWindow, serverWindow, sent, toClient: toClient.length, got: sim.consumedTotal, toServer: toServer.length, readDone, writeDone });
    }
    peer.poll();
    if (sent < toClient.length) {
      sent += sim.send(toClient.subarray(sent, sent + rng.int(1, 100_000)), rng.int(1, 0xffff));
    } else if (!completed) {
      peer.sendState(id, serverWriterDone);
      completed = true;
    }
    sim.consume(rng.int(0, 40_000));
    if (readDone && writeDone && sim.consumedTotal === toServer.length && sim.buffered === 0) break;
    await turn();
  }
  expect(await reading, `seed ${seed}: server-to-client bytes`).toEqual(toClient);
  expect(sim.received, `seed ${seed}: client-to-server bytes`).toEqual(toServer);
  expect(sim.violations, `seed ${seed}`).toEqual([]);
  expect(h.client.state, `seed ${seed}: session`).toBe(2);
  expect(budget.reserved, `seed ${seed}: budget`).toBe(baseline);
  await h.client.disconnect();
}

/**
 * Splits a framed item stream.
 * @param {Uint8Array} bytes
 * @returns {Uint8Array[]}
 */
function splitItems(bytes) {
  /** @type {Uint8Array[]} */
  const items = [];
  let offset = 0;
  while (offset < bytes.length) {
    const prefix = /** @type {{ length: number, size: number }} */ (readLength(bytes.subarray(offset, offset + 5)));
    items.push(bytes.subarray(offset + prefix.size, offset + prefix.size + prefix.length));
    offset += prefix.size + prefix.length;
  }
  return items;
}

/**
 * Mostly small items, some up to the window, some up to three windows.
 * @param {{ next(): number, int(min: number, max: number): number }} rng
 * @param {number} window
 */
function itemSize(rng, window) {
  const p = rng.next();
  if (p < 0.6) return rng.int(0, 200);
  if (p < 0.9) return rng.int(0, window);
  return rng.int(window, 3 * window);
}

/**
 * Channel items both ways, including items larger than either window: the server writes framed items in random
 * frames within its credit; the client reads with random batch sizes and writes with `write`/`writeMany`.
 * @param {number} seed
 */
async function runChannel(seed) {
  const rng = random(seed);
  const clientWindow = rng.int(4096, 40_000);
  const serverWindow = rng.int(1024, 40_000);
  const toClient = Array.from({ length: rng.int(0, 40) }, () => bytesOf(rng, itemSize(rng, clientWindow)));
  const toServer = Array.from({ length: rng.int(0, 40) }, () => bytesOf(rng, itemSize(rng, serverWindow)));
  const { h, peer, pipe, sim, id, budget } = await open(seed, clientWindow, serverWindow);
  const baseline = budget.reserved;
  const stream = frameItems(toClient.map((item) => pack((w) => w.writeBin(item))));
  const expectedToServer = frameItems(toServer.map((item) => pack((w) => w.writeBin(item)))).length;

  const channel = new NexusDuplexChannel(pipe, binFormatter);
  const reader = await channel.getReader();
  const writer = await channel.getWriter();

  const readRng = random(seed * 7 + 3);
  const reading = (async () => {
    /** @type {Uint8Array[]} */
    const got = [];
    for (;;) {
      if (readRng.next() < 0.5) {
        const r = await reader.read();
        if (r.done) break;
        got.push(/** @type {Uint8Array} */ (r.value));
      } else {
        const batch = await reader.readBatch(readRng.int(1, 5));
        if (batch.length === 0) break;
        for (const item of batch) got.push(/** @type {Uint8Array} */ (item));
      }
    }
    return got;
  })();

  const writeRng = random(seed * 7 + 4);
  const writing = (async () => {
    for (let i = 0; i < toServer.length;) {
      const n = writeRng.int(1, 4);
      if (n === 1) await writer.write(toServer[i]);
      else await writer.writeMany(toServer.slice(i, i + n));
      i += n;
    }
    await writer.complete();
  })();

  let readDone = false;
  let writeDone = false;
  void reading.then(() => { readDone = true; });
  void writing.then(() => { writeDone = true; });
  let sent = 0;
  let completed = false;
  for (let step = 0; ; step++) {
    if (step > MAX_STEPS) {
      throw stalled(seed, 'channel', { clientWindow, serverWindow, sent, toClient: stream.length, got: sim.consumedTotal, toServer: expectedToServer, readDone, writeDone });
    }
    peer.poll();
    if (sent < stream.length) {
      sent += sim.send(stream.subarray(sent, sent + rng.int(1, 50_000)), rng.int(1, 0xffff));
    } else if (!completed) {
      peer.sendState(id, serverWriterDone);
      completed = true;
    }
    sim.consume(rng.int(0, 30_000));
    if (readDone && writeDone && sim.consumedTotal === expectedToServer && sim.buffered === 0) break;
    await turn();
  }
  expect(await reading, `seed ${seed}: server-to-client items`).toEqual(toClient);
  const received = splitItems(sim.received).map((bytes) => /** @type {any} */ (testCodec.createReader(bytes)).readBin());
  expect(received, `seed ${seed}: client-to-server items`).toEqual(toServer);
  expect(sim.violations, `seed ${seed}`).toEqual([]);
  expect(h.client.state, `seed ${seed}: session`).toBe(2);
  expect(budget.reserved, `seed ${seed}: extra credit released`).toBe(baseline);
  await h.client.disconnect();
}

const SEEDS = Array.from({ length: 24 }, (_, i) => 1000 + i * 7919);

describe('flow-control properties', () => {
  it('raw pipes: no stall, the byte streams are preserved, the credit is never exceeded', async () => {
    for (const seed of SEEDS) await runRaw(seed);
    await tick();
    expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
  }, 120_000);

  it('channels: no stall with items smaller and larger than the window; items are preserved', async () => {
    for (const seed of SEEDS) await runChannel(seed);
    await tick();
    expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
  }, 120_000);
});
