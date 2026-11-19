import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { isCancellationError } from '../../src/contract/errors.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { PipeState } from '../../src/pipes/constants.js';
import { getPipeCounters } from '../../src/pipes/diagnostics.js';
import { NexusDuplexPipe } from '../../src/pipes/NexusDuplexPipe.js';
import { connectedHarness as connect, tick, until } from '../invocation/support/harness.js';
import { testCodec } from '../invocation/support/testCodec.js';
import { ServerPeer, pipeArgs, proxyOf } from './support/peer.js';

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

/** @param {Parameters<typeof connect>[0]} [options] */
async function connectedHarness(options) {
  const h = await connect(options);
  harnesses.push(h);
  return h;
}

/** @param {Harness} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
}

/**
 * @param {Harness} h @param {ServerPeer} peer @param {number} serverId @param {number} window
 */
async function openPipe(h, peer, serverId, window) {
  const pipe = h.client.createPipe();
  await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
  const sim = peer.accept(pipe.id, serverId, window);
  await pipe.ready;
  await tick();
  peer.poll();
  return { pipe, sim, id: pipe.id | (serverId << 8) };
}

const seq = (/** @type {number} */ n, start = 0) => Uint8Array.from({ length: n }, (_, i) => (start + i) & 0xff);
const writerDone = PipeState.Ready | PipeState.ClientWriterServerReaderComplete;
const readerDoneByServer = PipeState.Ready | PipeState.ClientReaderServerWriterComplete;

describe('forwardTo', () => {
  it('copies the source to the destination at the destination\'s pace, then half-closes the destination', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 8 * 1024 } });
    const peer = new ServerPeer(h);
    const a = await openPipe(h, peer, 1, 64 * 1024);
    const b = await openPipe(h, peer, 2, 1000);
    const data = seq(30_000);
    expect(a.sim.send(data)).toBe(8 * 1024);
    const forward = a.pipe.forwardTo(b.pipe);
    let sent = 8 * 1024;
    await until(() => {
      peer.poll();
      b.sim.consume(b.sim.buffered);
      sent += a.sim.send(data.subarray(sent));
      return sent === data.length && b.sim.received.length === data.length;
    }, 2000);
    expect(b.sim.received).toEqual(data);
    expect(b.sim.violations).toEqual([]);
    peer.sendState(a.id, readerDoneByServer);
    await forward;
    await tick();
    expect(peer.states().filter((s) => s.pipeId === b.id).at(-1)?.state).toBe(writerDone);
    // The source is not completed.
    expect(peer.states().filter((s) => s.pipeId === a.id)).toEqual([]);
    await finish(h);
  });

  it('stops on abort without completing either pipe', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openPipe(h, peer, 1, 1000);
    const b = await openPipe(h, peer, 2, 1000);
    const controller = new AbortController();
    const forward = a.pipe.forwardTo(b.pipe, { signal: controller.signal });
    await tick();
    controller.abort();
    await expect(forward).rejects.toSatisfy(isCancellationError);
    expect(peer.states()).toEqual([]);
    await expect(a.pipe.forwardTo(a.pipe)).rejects.toThrow(/itself/);
    await finish(h);
  });
});

describe('bridge', () => {
  it('forwards both ways and resolves once both directions half-closed', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openPipe(h, peer, 1, 64 * 1024);
    const b = await openPipe(h, peer, 2, 64 * 1024);
    const bridge = NexusDuplexPipe.bridge(a.pipe, b.pipe);
    a.sim.send(seq(100));
    b.sim.send(seq(50, 7));
    await until(() => { peer.poll(); return b.sim.buffered === 100 && a.sim.buffered === 50; });
    expect(b.sim.consume(100)).toEqual(seq(100));
    expect(a.sim.consume(50)).toEqual(seq(50, 7));
    peer.sendState(a.id, readerDoneByServer);
    peer.sendState(b.id, readerDoneByServer);
    await bridge;
    await tick();
    // The server completed its writers, so half-closing ours completes both pipes.
    expect(peer.states().filter((s) => s.pipeId === a.id).at(-1)?.state).toBe(PipeState.Complete);
    expect(peer.states().filter((s) => s.pipeId === b.id).at(-1)?.state).toBe(PipeState.Complete);
    await finish(h);
  });

  it('completes both pipes when one ends abnormally', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openPipe(h, peer, 1, 1000);
    const b = await openPipe(h, peer, 2, 1000);
    const bridge = a.pipe.bridge(b.pipe);
    await tick();
    peer.sendState(a.id, PipeState.Complete);
    await bridge;
    await tick();
    expect(peer.states().filter((s) => s.pipeId === b.id).at(-1)?.state).toBe(PipeState.Complete);
    await finish(h);
  });

  it('an abort stops both directions without completing either pipe', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openPipe(h, peer, 1, 1000);
    const b = await openPipe(h, peer, 2, 1000);
    const controller = new AbortController();
    const bridge = a.pipe.bridge(b.pipe, { signal: controller.signal });
    await tick();
    controller.abort();
    await expect(bridge).rejects.toSatisfy(isCancellationError);
    expect(peer.states()).toEqual([]);
    await finish(h);
  });
});
