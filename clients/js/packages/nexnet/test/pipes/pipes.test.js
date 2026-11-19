import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { NexusBusyError, NexusError, NexusTransportError, isCancellationError } from '../../src/contract/errors.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationCounters } from '../../src/invocation/diagnostics.js';
import { decodeInvocation } from '../../src/invocation/messages.js';
import { PipeState } from '../../src/pipes/constants.js';
import { getPipeCounters } from '../../src/pipes/diagnostics.js';
import { NexusDuplexPipe } from '../../src/pipes/NexusDuplexPipe.js';
import { MessageType } from '../../src/session/protocol.js';
import { connectedHarness as connect, tick, until } from '../invocation/support/harness.js';
import { pack, testCodec } from '../invocation/support/testCodec.js';
import { ServerPeer, payload, pipeArgs, proxyOf } from './support/peer.js';

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
/** @type {Harness[]} */
let harnesses = [];
beforeEach(() => {
  previousCodec = setInvocationCodec(testCodec);
});
afterEach(async () => {
  // A failed test must not leave its session (and the process-wide counters) behind.
  for (const h of harnesses) await h.client.disconnect();
  harnesses = [];
  setInvocationCodec(previousCodec);
});

/**
 * A connected client whose session the test's cleanup ends.
 * @param {Parameters<typeof connect>[0]} [options]
 */
async function connectedHarness(options) {
  const h = await connect(options);
  harnesses.push(h);
  return h;
}

const KiB = 1024;

/** @typedef {Awaited<ReturnType<typeof connect>>} Harness */

/**
 * Disconnects and checks the leak-audit counters.
 * @param {Harness} h
 */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
  expect(getInvocationCounters().pendingInvocations).toBe(0);
}

/**
 * Opens a pipe from the client: createPipe, the pipe method, the server's Ready.
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

/** @param {ReadableStreamBYOBReader} reader @param {number} n */
async function readExactly(reader, n) {
  const out = new Uint8Array(n);
  let got = 0;
  while (got < n) {
    const { value, done } = await reader.read(new Uint8Array(n - got));
    if (done || !value) throw new Error(`stream ended after ${got} of ${n} bytes`);
    out.set(value, got);
    got += value.length;
  }
  return out;
}

const seq = (/** @type {number} */ n, start = 0) => Uint8Array.from({ length: n }, (_, i) => (start + i) & 0xff);

describe('opening a pipe (client opens)', () => {
  it('sends the pipe method as IgnoreReturn | DuplexPipe with the local ID, then grants its window on Ready', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 8 * KiB } });
    const peer = new ServerPeer(h);
    const pipe = h.client.createPipe();
    expect(pipe).toBeInstanceOf(NexusDuplexPipe);
    expect(pipe.id).toBe(1);
    expect(getPipeCounters().openPipes).toBe(1);
    await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
    const invocation = decodeInvocation(peer.framesOf(MessageType.Invocation)[0].body);
    expect(invocation).toMatchObject({ invocationId: 0, methodId: 5, flags: 3 });
    expect(invocation.args).toEqual(pack((w) => { w.writeArrayHeader(1); w.writeInt(1); }));

    // No grant before Ready.
    expect(peer.framesOf(MessageType.PipeWindowUpdate)).toEqual([]);
    peer.accept(1, 9, 4 * KiB);
    await expect(pipe.ready).resolves.toBeUndefined();
    await tick();
    const updates = peer.framesOf(MessageType.PipeWindowUpdate);
    expect(updates).toHaveLength(1);
    expect(updates[0].pipeId).toBe(1 | (9 << 8));
    expect(new DataView(updates[0].body.buffer).getUint32(0, true)).toBe(8 * KiB);
    await finish(h);
  });

  it('a refusal fails ready with NexusBusyError and frees the pipe', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const pipe = h.client.createPipe();
    await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
    peer.sendState(pipe.id, PipeState.Refused);
    await expect(pipe.ready).rejects.toBeInstanceOf(NexusBusyError);
    await expect(pipe.completed).resolves.toBeUndefined();
    expect(getPipeCounters().openPipes).toBe(0);
    // The ID is free again; the session is up.
    const next = h.client.createPipe();
    expect(next.id).toBe(2);
    await next[Symbol.asyncDispose]();
    expect(getPipeCounters().openPipes).toBe(0);
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('maxPipes and the session budget refuse createPipe with NexusBusyError', async () => {
    const h = await connectedHarness({ config: { maxPipes: 2 } });
    const a = h.client.createPipe();
    const b = h.client.createPipe();
    expect(() => h.client.createPipe()).toThrow(NexusBusyError);
    await a[Symbol.asyncDispose]();
    const c = h.client.createPipe();
    expect(c.id).toBe(3);
    await b[Symbol.asyncDispose]();
    await c[Symbol.asyncDispose]();
    await finish(h);
  });

  it('a pipe can be used once, only on its own session', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe } = await openPipe(h, peer);
    await expect(proxyOf(h)._invokePipe(5, pipe, pipeArgs)).rejects.toThrow(/only be used once/);
    await expect(proxyOf(h)._invokePipe(5, /** @type {any} */ ({}), pipeArgs)).rejects.toThrow(TypeError);
    await finish(h);
  });

  it('a cancel before send rejects, sends nothing and fails the pipe; an aborted signal leaves the pipe unused', async () => {
    const h = await connectedHarness({ greeting: { invocationCredits: 1 } });
    const peer = new ServerPeer(h);
    const first = h.client.createPipe();
    await proxyOf(h)._invokePipe(5, first, pipeArgs); // Takes the only credit.

    const aborted = h.client.createPipe();
    await expect(proxyOf(h)._invokePipe(5, aborted, pipeArgs, { signal: AbortSignal.abort() })).rejects.toSatisfy(isCancellationError);

    const pipe = h.client.createPipe();
    const controller = new AbortController();
    const call = proxyOf(h)._invokePipe(5, pipe, pipeArgs, { signal: controller.signal });
    controller.abort();
    await expect(call).rejects.toSatisfy(isCancellationError);
    await expect(pipe.ready).rejects.toSatisfy(isCancellationError);
    expect(peer.framesOf(MessageType.Invocation)).toHaveLength(1);
    // The unused pipe can still be sent.
    expect(getPipeCounters().openPipes).toBe(2);
    await aborted[Symbol.asyncDispose]();
    await first[Symbol.asyncDispose]();
    await finish(h);
  });

  it('createPipe throws NexusTransportError when not connected', async () => {
    const h = await connectedHarness();
    await h.client.disconnect();
    expect(() => h.client.createPipe()).toThrow(NexusTransportError);
    expect(() => h.client.createChannel(/** @type {any} */ ({ read() {}, write() {} }))).toThrow(NexusTransportError);
    await finish(h);
  });

  it('a Ready that matches no pending pipe is a protocol error', async () => {
    const h = await connectedHarness();
    const session = h.session;
    const peer = new ServerPeer(h);
    peer.sendState(4 | (9 << 8), PipeState.Ready, 1024);
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.ProtocolError);
    await finish(h);
  });
});

describe('reading', () => {
  it('delivers bytes in order and grants credit only as they are read, half the window at a time', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 8 * KiB } });
    const peer = new ServerPeer(h);
    const { pipe, sim, id } = await openPipe(h, peer);
    const grants = () => peer.framesOf(MessageType.PipeWindowUpdate).filter((f) => f.pipeId === id).length;
    expect(grants()).toBe(1); // The opening grant.

    expect(sim.send(seq(8 * KiB))).toBe(8 * KiB);
    await tick();
    // Nothing read, nothing granted.
    expect(grants()).toBe(1);
    expect(getPipeCounters().bufferedBytes).toBe(8 * KiB);

    const reader = pipe.readable.getReader({ mode: 'byob' });
    const got = await readExactly(reader, 4 * KiB - 1);
    await tick();
    expect(got).toEqual(seq(4 * KiB - 1));
    expect(grants()).toBe(1);
    expect(sim.send(seq(1))).toBe(0); // Still no credit.

    await readExactly(reader, 1);
    await tick();
    expect(grants()).toBe(2);
    expect(sim.sendCredit).toBe(4 * KiB);
    reader.releaseLock();
    await finish(h);
  });

  it('supports BYOB readers', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim } = await openPipe(h, peer);
    sim.send(seq(10));
    const reader = pipe.readable.getReader({ mode: 'byob' });
    const first = await reader.read(new Uint8Array(4));
    expect(first.value).toEqual(seq(4));
    const second = await reader.read(new Uint8Array(100));
    expect(second.value).toEqual(seq(6, 4));
    reader.releaseLock();
    await finish(h);
  });

  it('ends the stream after buffered data when the server completes its writer', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim, id } = await openPipe(h, peer);
    sim.send(seq(5));
    peer.sendState(id, PipeState.Ready | PipeState.ClientReaderServerWriterComplete);
    const chunks = [];
    for await (const chunk of pipe.readable) chunks.push(...chunk);
    expect(chunks).toEqual([...seq(5)]);
    await finish(h);
  });

  it('cancelling the readable completes this side\'s reading direction and discards later data within the credit', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 8 * KiB } });
    const peer = new ServerPeer(h);
    const { pipe, sim, id } = await openPipe(h, peer);
    sim.send(seq(100));
    await pipe.readable.cancel();
    await tick();
    const last = peer.states().at(-1);
    expect(last).toEqual({ pipeId: id, state: PipeState.Ready | PipeState.ClientReaderServerWriterComplete, window: 0 });
    expect(getPipeCounters().bufferedBytes).toBe(0);
    sim.send(seq(100));
    await tick();
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('data beyond the credit disconnects with FlowControl', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 4 * KiB } });
    const session = h.session;
    const peer = new ServerPeer(h);
    const { id } = await openPipe(h, peer);
    peer.sendData(id, seq(4 * KiB + 1));
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.FlowControl);
    await finish(h);
  });

  it('data for an unknown pipe is a protocol error; a window update for one is ignored', async () => {
    const h = await connectedHarness();
    const session = h.session;
    const peer = new ServerPeer(h);
    peer.sendWindowUpdate(3 | (4 << 8), 100);
    await tick();
    expect(h.client.state).toBe(2);
    peer.sendData(3 | (4 << 8), seq(1));
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.ProtocolError);
    await finish(h);
  });

  it('credit granted beyond 2^31 disconnects with FlowControl', async () => {
    const h = await connectedHarness();
    const session = h.session;
    const peer = new ServerPeer(h);
    const { id } = await openPipe(h, peer, { window: 2 ** 31 - 10 });
    peer.sendWindowUpdate(id, 11);
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.FlowControl);
    await finish(h);
  });
});

describe('writing', () => {
  it('splits writes at the credit and the frame size, and waits for credit', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim, id } = await openPipe(h, peer, { window: 100 * KiB });
    const writer = pipe.writable.getWriter();
    const data = seq(150 * KiB);
    let written = false;
    const write = writer.write(data).then(() => { written = true; });
    await tick();
    const frames = peer.framesOf(MessageType.DuplexPipeWrite).filter((f) => f.pipeId === id);
    expect(frames.map((f) => f.body.length)).toEqual([0xffff, 100 * KiB - 0xffff]);
    expect(written).toBe(false);

    sim.consume(100 * KiB);
    await write;
    expect(payload(peer.framesOf(MessageType.DuplexPipeWrite))).toEqual(data);
    expect(sim.violations).toEqual([]);

    await writer.close();
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Ready | PipeState.ClientWriterServerReaderComplete, window: 0 });
    await finish(h);
  });

  it('writes made before Ready wait for the server\'s window', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const pipe = h.client.createPipe();
    const writer = pipe.writable.getWriter();
    const write = writer.write(seq(10));
    await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
    await tick();
    expect(peer.framesOf(MessageType.DuplexPipeWrite)).toEqual([]);
    peer.accept(pipe.id, 9, 1024);
    await write;
    expect(payload(peer.framesOf(MessageType.DuplexPipeWrite))).toEqual(seq(10));
    writer.releaseLock();
    await finish(h);
  });

  it('the writable errors when the server completes its reading direction', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, id } = await openPipe(h, peer, { window: 10 });
    const writer = pipe.writable.getWriter();
    await writer.write(seq(10));
    const blocked = writer.write(seq(10));
    peer.sendState(id, PipeState.Ready | PipeState.ClientWriterServerReaderComplete);
    await expect(blocked).rejects.toThrow(NexusError);
    await expect(writer.closed).rejects.toThrow(/writing direction is complete/);
    await finish(h);
  });

  it('pipeTo from a ReadableStream delivers everything', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim } = await openPipe(h, peer, { window: 16 * KiB });
    const source = new ReadableStream({
      start(controller) {
        for (let i = 0; i < 10; i++) controller.enqueue(seq(10 * KiB, i));
        controller.close();
      },
    });
    const piping = source.pipeTo(pipe.writable);
    await until(() => { peer.poll(); sim.consume(sim.buffered); return sim.received.length === 100 * KiB; }, 500);
    await piping;
    const expected = new Uint8Array(100 * KiB);
    for (let i = 0; i < 10; i++) expected.set(seq(10 * KiB, i), i * 10 * KiB);
    expect(sim.received).toEqual(expected);
    expect(sim.violations).toEqual([]);
    await finish(h);
  });
});

describe('closing', () => {
  it('complete() sends Complete; the pipe is freed once the server confirms and it is disposed', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, id } = await openPipe(h, peer);
    await pipe.complete();
    await expect(pipe.completed).resolves.toBeUndefined();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Complete, window: 0 });
    await pipe[Symbol.asyncDispose]();
    expect(getPipeCounters().openPipes).toBe(1); // Closing until the server confirms.
    peer.sendState(id, PipeState.Complete);
    await tick();
    expect(getPipeCounters().openPipes).toBe(0);
    expect(peer.states().filter((s) => s.state === PipeState.Complete)).toHaveLength(1);
    await finish(h);
  });

  it('answers the server\'s Complete with its own', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, id } = await openPipe(h, peer);
    peer.sendState(id, PipeState.Complete);
    await expect(pipe.completed).resolves.toBeUndefined();
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Complete, window: 0 });
    await pipe[Symbol.asyncDispose]();
    expect(getPipeCounters().openPipes).toBe(0);
    await finish(h);
  });

  it('data after the server\'s Complete is a flow-control violation', async () => {
    const h = await connectedHarness();
    const session = h.session;
    const peer = new ServerPeer(h);
    const { id } = await openPipe(h, peer);
    peer.sendState(id, PipeState.Complete);
    peer.sendData(id, seq(1));
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.FlowControl);
    await finish(h);
  });

  it('disposing a pipe never sent frees it at once; one in flight closes when Ready arrives', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const unused = h.client.createPipe();
    await unused[Symbol.asyncDispose]();
    expect(getPipeCounters().openPipes).toBe(0);
    await expect(unused.ready).rejects.toSatisfy(isCancellationError);

    const pipe = h.client.createPipe();
    await proxyOf(h)._invokePipe(5, pipe, pipeArgs);
    await pipe[Symbol.asyncDispose]();
    await expect(pipe.ready).rejects.toSatisfy(isCancellationError);
    expect(getPipeCounters().openPipes).toBe(1);
    peer.accept(pipe.id, 3, 1024);
    await tick();
    const id = pipe.id | (3 << 8);
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Complete, window: 0 });
    peer.sendState(id, PipeState.Complete);
    await tick();
    expect(getPipeCounters().openPipes).toBe(0);
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('a pipe left closing past the progress timeout disconnects with Stalled', async () => {
    const h = await connectedHarness({ config: { progressTimeout: 1000, pingInterval: 500, timeout: 100_000 } });
    const session = h.session;
    const peer = new ServerPeer(h);
    const { pipe } = await openPipe(h, peer);
    await pipe.complete();
    h.clock.advance(600);
    await tick();
    expect(h.client.state).toBe(2);
    h.clock.advance(1000);
    await until(() => h.client.state === 5);
    expect(session.disconnectReason).toBe(DisconnectReason.Stalled);
    expect(h.logs.some(([, message]) => /pipe P\d+ closing/.test(message))).toBe(true);
    await finish(h);
  });

  it('when the session ends, open pipes complete and pending ones fail as canceled with the reason as cause', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim } = await openPipe(h, peer);
    sim.send(seq(3));
    await tick();
    const pending = h.client.createPipe();
    await proxyOf(h)._invokePipe(5, pending, pipeArgs);
    h.transport.serverClose();
    await until(() => h.client.state === 5);
    await expect(pipe.completed).resolves.toBeUndefined();
    const error = await pending.ready.catch((e) => e);
    expect(isCancellationError(error)).toBe(true);
    expect(error.cause).toBeInstanceOf(NexusTransportError);
    expect(error.cause.reason).toBe(DisconnectReason.SocketError);
    // Buffered data stays readable, then the stream ends.
    const chunks = [];
    for await (const chunk of pipe.readable) chunks.push(...chunk);
    expect(chunks).toEqual([...seq(3)]);
    await expect(pipe.writable.getWriter().write(seq(1))).rejects.toThrow(NexusTransportError);
    await finish(h);
  });
});

describe('registering a pipe (server opens)', () => {
  it('sends Ready with the full ID and window, frees the execution slot, and closes when the method returns', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 8 * KiB, maxConcurrentConnectionInvocations: 1 } });
    const peer = new ServerPeer(h);
    /** @type {(() => void) | null} */
    let finishMethod = null;
    /** @type {NexusDuplexPipe | null} */
    let received = null;
    h.nexus.methods.set(1, async (reader, ctx) => {
      reader.readArrayHeader();
      received = ctx.pipe(reader.readInt());
      await new Promise((resolve) => { finishMethod = () => resolve(undefined); });
    });
    h.nexus.methods.set(2, () => {});
    peer.invokePipeMethod(1, 7);
    await until(() => received !== null);
    await tick();
    const ready = peer.states()[0];
    expect(ready).toEqual({ pipeId: 1 | (7 << 8), state: PipeState.Ready, window: 8 * KiB });
    const pipe = /** @type {NexusDuplexPipe} */ (/** @type {unknown} */ (received));
    await expect(pipe.ready).resolves.toBeUndefined();

    // The slot is free: another invocation runs while the pipe method waits.
    peer.invokePipeMethod(2, 8);
    await until(() => h.nexus.dispatched.length === 2);

    const sim = peer.adopt(ready.pipeId, ready.window, 4 * KiB);
    await tick();
    const writer = pipe.writable.getWriter();
    await writer.write(seq(100));
    peer.poll();
    expect(sim.buffered).toBe(100);
    writer.releaseLock();

    /** @type {() => void} */ (/** @type {unknown} */ (finishMethod))();
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: ready.pipeId, state: PipeState.Complete, window: 0 });
    peer.sendState(ready.pipeId, PipeState.Complete);
    await tick();
    expect(getPipeCounters().openPipes).toBe(0);
    await finish(h);
  });

  it('refuses with the opener\'s partial ID when maxPipes is reached; the session stays up', async () => {
    const h = await connectedHarness({ config: { maxPipes: 1 } });
    const peer = new ServerPeer(h);
    /** @type {unknown} */
    let error = null;
    h.nexus.methods.set(1, (reader, ctx) => {
      reader.readArrayHeader();
      try {
        ctx.pipe(reader.readInt());
      } catch (e) {
        error = e;
        throw e;
      }
    });
    const held = h.client.createPipe();
    peer.invokePipeMethod(1, 7);
    await until(() => error !== null);
    await tick();
    expect(error).toBeInstanceOf(NexusBusyError);
    expect(peer.states()).toEqual([{ pipeId: 7 << 8, state: PipeState.Refused, window: 0 }]);
    expect(h.client.state).toBe(2);
    await held[Symbol.asyncDispose]();
    await finish(h);
  });
});

describe('counters', () => {
  it('count open pipes and buffered bytes, and return to zero', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { pipe, sim, id } = await openPipe(h, peer);
    sim.send(seq(10));
    await tick();
    expect(getPipeCounters()).toEqual({ openPipes: 1, bufferedBytes: 10 });
    const reader = pipe.readable.getReader();
    await reader.read();
    expect(getPipeCounters()).toEqual({ openPipes: 1, bufferedBytes: 0 });
    reader.releaseLock();
    await pipe[Symbol.asyncDispose]();
    peer.sendState(id, PipeState.Complete);
    await tick();
    expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
    await finish(h);
  });
});
