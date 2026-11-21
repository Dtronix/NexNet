import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NexusError, NexusMessageTooLargeError, isCancellationError } from '../../src/contract/errors.js';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { getInvocationManager } from '../../src/invocation/InvocationManager.js';
import { PipeState } from '../../src/pipes/constants.js';
import { NexusChannelReader, NexusChannelWriter, NexusDuplexChannel } from '../../src/pipes/channels.js';
import { getPipeCounters } from '../../src/pipes/diagnostics.js';
import { MessageType } from '../../src/session/protocol.js';
import { connectedHarness as connect, tick, until } from '../invocation/support/harness.js';
import { binFormatter, intFormatter, pack, testCodec } from '../invocation/support/testCodec.js';
import { ServerPeer, frameItems, pipeArgs, proxyOf } from './support/peer.js';

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

const KiB = 1024;

/** @param {Harness} h */
async function finish(h) {
  await h.client.disconnect();
  await tick();
  expect(getPipeCounters()).toEqual({ openPipes: 0, bufferedBytes: 0 });
}

/**
 * Opens a channel from the client.
 * @template T
 * @param {Harness} h
 * @param {ServerPeer} peer
 * @param {import('../../src/contract/codec.js').Formatter<T>} formatter
 * @param {number} [window] The server's receive window.
 */
async function openChannel(h, peer, formatter, window = 64 * KiB) {
  const channel = h.client.createChannel(formatter);
  expect(channel).toBeInstanceOf(NexusDuplexChannel);
  await proxyOf(h)._invokeChannel(6, channel, pipeArgs);
  const sim = peer.accept(channel.pipe.id, 2, window);
  const reader = await channel.getReader();
  const writer = await channel.getWriter();
  await tick();
  peer.poll();
  return { channel, sim, reader, writer, id: channel.pipe.id | (2 << 8) };
}

const int = (/** @type {number} */ v) => pack((w) => w.writeInt(v));
const bin = (/** @type {number} */ n, fill = 1) => pack((w) => w.writeBin(new Uint8Array(n).fill(fill)));

describe('channel writer', () => {
  it('frames each item with its varint length; writeMany sends one write', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { channel, sim, writer } = await openChannel(h, peer, intFormatter);
    expect(await channel.getWriter()).toBe(writer);
    await writer.write(1);
    await writer.writeMany([2, 300, 70000]);
    peer.poll();
    const writes = peer.framesOf(MessageType.DuplexPipeWrite);
    expect(writes).toHaveLength(2);
    expect(writes[1].body).toEqual(frameItems([int(2), int(300), int(70000)]));
    expect(sim.consume(sim.buffered)).toEqual(frameItems([int(1), int(2), int(300), int(70000)]));
    await finish(h);
  });

  it('rejects an item larger than the peer\'s maxMessageSize without sending it; the channel stays usable', async () => {
    const h = await connectedHarness({ greeting: { maxMessageSize: 65526 } });
    const peer = new ServerPeer(h);
    const { sim, writer } = await openChannel(h, peer, binFormatter, 256 * KiB);
    await expect(writer.write(new Uint8Array(65526))).rejects.toBeInstanceOf(NexusMessageTooLargeError);
    await expect(writer.writeMany([new Uint8Array(1), new Uint8Array(70000)])).rejects.toBeInstanceOf(NexusMessageTooLargeError);
    await writer.write(new Uint8Array(3).fill(9));
    peer.poll();
    expect(sim.takeItems()).toEqual([bin(3, 9)]);
    await finish(h);
  });

  it('writeAndComplete sends the items, then completes the writing direction', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { sim, writer, id } = await openChannel(h, peer, intFormatter);
    await writer.writeAndComplete([4, 5]);
    await tick();
    peer.poll();
    expect(sim.takeItems()).toEqual([int(4), int(5)]);
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Ready | PipeState.ClientWriterServerReaderComplete, window: 0 });
    await expect(writer.write(6)).rejects.toThrow(NexusError);
    await finish(h);
  });

  it('an abort while waiting for credit rejects; after a partial send it also completes the writing direction', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { sim, writer, id } = await openChannel(h, peer, binFormatter, 100);
    await writer.write(new Uint8Array(97)); // 2 + 1 + 97 bytes: exactly the credit.
    const before = new AbortController();
    const waiting = writer.write(new Uint8Array(1), { signal: before.signal });
    before.abort();
    await expect(waiting).rejects.toSatisfy(isCancellationError);
    expect(peer.states().some((s) => s.state & PipeState.ClientWriterServerReaderComplete)).toBe(false);

    sim.consume(50);
    sim.grantNow();
    await tick();
    const partial = new AbortController();
    const split = writer.write(new Uint8Array(200), { signal: partial.signal });
    await tick();
    partial.abort();
    await expect(split).rejects.toSatisfy(isCancellationError);
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Ready | PipeState.ClientWriterServerReaderComplete, window: 0 });
    await finish(h);
  });
});

describe('channel reader', () => {
  it('reads items one at a time, in batches, through the async iterator, and until complete', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { channel, sim, reader, id } = await openChannel(h, peer, intFormatter);
    expect(await channel.getReader()).toBe(reader);
    sim.send(frameItems([1, 2, 3, 4, 5, 6, 7].map(int)));
    expect(await reader.read()).toEqual({ value: 1, done: false });
    expect(await reader.readBatch(2)).toEqual([2, 3]);
    /** @type {number[]} */
    const iterated = [];
    for await (const value of reader) {
      iterated.push(value);
      if (value === 5) break;
    }
    expect(iterated).toEqual([4, 5]);
    sim.send(frameItems([int(8)]));
    peer.sendState(id, PipeState.Ready | PipeState.ClientReaderServerWriterComplete);
    expect(await reader.readUntilComplete()).toEqual([6, 7, 8]);
    expect(reader.isComplete).toBe(true);
    expect(await reader.read()).toEqual({ value: undefined, done: true });
    expect(await reader.readBatch(5)).toEqual([]);
    await finish(h);
  });

  it('a read waits for an item split across frames and can be aborted', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { sim, reader } = await openChannel(h, peer, binFormatter);
    const item = frameItems([bin(300, 4)]);
    const controller = new AbortController();
    const aborted = reader.read({ signal: controller.signal });
    controller.abort();
    await expect(aborted).rejects.toSatisfy(isCancellationError);

    const read = reader.read();
    sim.send(item.subarray(0, 1));
    await tick();
    sim.send(item.subarray(1, 100));
    await tick();
    sim.send(item.subarray(100));
    expect((await read).value).toEqual(new Uint8Array(300).fill(4));
    await finish(h);
  });

  it('grants consumed credit early when the writer lacks credit for the rest of an item (no half-window stall)', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 4 * KiB } });
    const peer = new ServerPeer(h);
    const { sim, reader, id } = await openChannel(h, peer, binFormatter);
    const a = bin(2000, 1);
    const b = bin(3000, 2);
    const stream = frameItems([a, b]);
    expect(sim.send(stream)).toBe(4 * KiB);
    const grants = () => peer.framesOf(MessageType.PipeWindowUpdate).filter((f) => f.pipeId === id).map((f) => new DataView(f.body.buffer).getUint32(0, true));
    expect((await reader.read()).value).toEqual(new Uint8Array(2000).fill(1));
    const second = reader.read();
    await tick();
    // Item a (2,003 bytes framed) is below half the window, but item b needs more than the credit left.
    expect(grants().at(-1)).toBe(a.length + 2);
    sim.send(stream.subarray(4 * KiB));
    expect((await second).value).toEqual(new Uint8Array(3000).fill(2));
    await finish(h);
  });

  it('reserves extra credit from the session budget for an item larger than the window, and releases it as consumed', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 4 * KiB } });
    const peer = new ServerPeer(h);
    const budget = /** @type {import('../../src/invocation/InvocationManager.js').InvocationManager} */ (getInvocationManager(h.session)).budget;
    const { sim, reader } = await openChannel(h, peer, binFormatter);
    const baseline = budget.reserved;
    const big = frameItems([bin(10 * KiB, 7)]);
    const read = reader.read();
    let sent = sim.send(big);
    expect(sent).toBe(4 * KiB);
    await until(() => { peer.poll(); return sim.sendCredit > 0; });
    expect(budget.reserved).toBe(baseline + big.length - 4 * KiB);
    sent += sim.send(big.subarray(sent));
    expect(sent).toBe(big.length);
    expect((await read).value).toEqual(new Uint8Array(10 * KiB).fill(7));
    expect(budget.reserved).toBe(baseline);
    await finish(h);
  });

  it('a length over maxMessageSize resets the pipe and rejects; the session stays up', async () => {
    const h = await connectedHarness({ config: { maxMessageSize: 64 * KiB } });
    const peer = new ServerPeer(h);
    const { sim, reader, id } = await openChannel(h, peer, binFormatter);
    sim.send(Uint8Array.of(0x81, 0x80, 0x04)); // 65,537
    await expect(reader.read()).rejects.toBeInstanceOf(NexusMessageTooLargeError);
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Complete, window: 0 });
    expect(h.client.state).toBe(2);
    expect(await reader.read()).toEqual({ value: undefined, done: true });
    await finish(h);
  });

  it('an item that does not decode to exactly its length resets the pipe', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { sim, reader, id } = await openChannel(h, peer, intFormatter);
    sim.send(Uint8Array.of(2, 0x01, 0x02));
    await expect(reader.read()).rejects.toThrow(/exactly its length/);
    await tick();
    expect(peer.states().at(-1)).toEqual({ pipeId: id, state: PipeState.Complete, window: 0 });
    expect(h.client.state).toBe(2);
    await finish(h);
  });

  it('a malformed prefix resets the pipe', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const { sim, reader } = await openChannel(h, peer, intFormatter);
    sim.send(Uint8Array.of(0x80, 0x80, 0x80, 0x80, 0x80, 0x01));
    await expect(reader.read()).rejects.toThrow(/longer than five bytes/);
    await finish(h);
  });
});

describe('channel forwardTo', () => {
  const writerDone = PipeState.Ready | PipeState.ClientWriterServerReaderComplete;
  const serverWriterDone = PipeState.Ready | PipeState.ClientReaderServerWriterComplete;
  /** @param {ServerPeer} peer @param {number} id */
  const statesOf = (peer, id) => peer.states().filter((s) => s.pipeId === id);

  it('forwards items as they arrive at the destination\'s pace, then half-closes the destination', async () => {
    const h = await connectedHarness({ config: { pipeWindow: 4 * KiB } });
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, binFormatter);
    const b = await openChannel(h, peer, binFormatter, 1000);
    // Small items, an item larger than both windows, and a zero-length item.
    const items = [bin(10, 1), bin(200, 2), bin(10 * KiB, 3), bin(0), bin(300, 4)];
    const stream = frameItems(items);
    let sent = a.sim.send(stream.subarray(0, 1)); // A split prefix first.
    const forward = a.channel.forwardTo(b.channel);
    await tick();
    await until(() => {
      peer.poll();
      b.sim.consume(b.sim.buffered);
      sent += a.sim.send(stream.subarray(sent), 777);
      return sent === stream.length && b.sim.received.length === stream.length;
    }, 2000);
    expect(b.sim.received).toEqual(stream);
    expect(b.sim.violations).toEqual([]);
    expect(a.sim.violations).toEqual([]);
    peer.sendState(a.id, serverWriterDone);
    await forward;
    await tick();
    expect(statesOf(peer, b.id).at(-1)?.state).toBe(writerDone);
    // The source is not completed.
    expect(statesOf(peer, a.id)).toEqual([]);
    await finish(h);
  });

  it('an item larger than the destination accepts rejects; the items before it were sent and it stays readable', async () => {
    const h = await connectedHarness({ greeting: { maxMessageSize: 65526 }, config: { pipeWindow: 256 * KiB } });
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, binFormatter, 256 * KiB);
    const b = await openChannel(h, peer, binFormatter, 256 * KiB);
    a.sim.send(frameItems([bin(10, 1), bin(70000, 2), bin(5, 3)]));
    await expect(a.channel.forwardTo(b.channel)).rejects.toBeInstanceOf(NexusMessageTooLargeError);
    peer.poll();
    expect(b.sim.takeItems()).toEqual([bin(10, 1)]);
    expect(peer.states()).toEqual([]);
    expect((await a.reader.read()).value).toEqual(new Uint8Array(70000).fill(2));
    expect((await a.reader.read()).value).toEqual(new Uint8Array(5).fill(3));
    await finish(h);
  });

  it('passes an incomplete item at the end of the source on as it is', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, binFormatter);
    const b = await openChannel(h, peer, binFormatter);
    const partial = frameItems([bin(4, 1), bin(100, 2)]).subarray(0, 50);
    a.sim.send(partial);
    peer.sendState(a.id, serverWriterDone);
    await a.channel.forwardTo(b.channel);
    await tick();
    peer.poll();
    expect(b.sim.consume(b.sim.buffered)).toEqual(partial);
    expect(statesOf(peer, b.id).at(-1)?.state).toBe(writerDone);
    await finish(h);
  });

  it('resolves at the next data once the destination\'s output was completed by its peer', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, intFormatter);
    const b = await openChannel(h, peer, intFormatter);
    const forward = a.channel.forwardTo(b.channel);
    a.sim.send(frameItems([int(1)]));
    await until(() => { peer.poll(); return b.sim.buffered > 0; });
    peer.sendState(b.id, writerDone);
    await tick();
    a.sim.send(frameItems([int(2)]));
    await forward;
    peer.poll();
    expect(b.sim.takeItems()).toEqual([int(1)]);
    expect(await a.reader.read()).toEqual({ value: 2, done: false });
    expect(statesOf(peer, a.id)).toEqual([]);
    await finish(h);
  });

  it('a malformed prefix rejects without completing either channel', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, intFormatter);
    const b = await openChannel(h, peer, intFormatter);
    a.sim.send(Uint8Array.of(0x80, 0x80, 0x80, 0x80, 0x80, 0x01));
    await expect(a.channel.forwardTo(b.channel)).rejects.toThrow(NexusError);
    expect(peer.states()).toEqual([]);
    await finish(h);
  });

  it('stops on abort without completing either channel; rejects itself and non-channels', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    const a = await openChannel(h, peer, intFormatter);
    const b = await openChannel(h, peer, intFormatter, 2);
    a.sim.send(frameItems([int(1), int(2), int(3)]));
    const controller = new AbortController();
    const forward = a.channel.forwardTo(b.channel, { signal: controller.signal });
    // The destination's 2 bytes of credit take the first item only; the forward waits for more.
    await until(() => { peer.poll(); return b.sim.buffered === 2; });
    controller.abort();
    await expect(forward).rejects.toSatisfy(isCancellationError);
    expect(peer.states()).toEqual([]);
    expect(b.sim.takeItems()).toEqual([int(1)]);
    // The source's input is positioned after what was forwarded.
    expect(await a.reader.readBatch(5)).toEqual([2, 3]);
    await expect(a.channel.forwardTo(a.channel)).rejects.toThrow(/itself/);
    await expect(a.channel.forwardTo(/** @type {any} */ (b.channel.pipe))).rejects.toThrow(TypeError);
    const aborted = new AbortController();
    aborted.abort();
    await expect(a.channel.forwardTo(b.channel, { signal: aborted.signal })).rejects.toSatisfy(isCancellationError);
    await finish(h);
  });
});

describe('channels the server opens', () => {
  it('ctx.channel registers the pipe; reading and writing work', async () => {
    const h = await connectedHarness();
    const peer = new ServerPeer(h);
    /** @type {number[]} */
    const got = [];
    h.nexus.methods.set(1, async (r, ctx) => {
      r.readArrayHeader();
      const channel = ctx.channel(r.readInt(), intFormatter);
      const reader = await channel.getReader();
      const writer = await channel.getWriter();
      for await (const value of reader) {
        got.push(value);
        await writer.write(value * 2);
      }
    });
    peer.invokePipeMethod(1, 5);
    await until(() => peer.states().length > 0);
    const ready = peer.states()[0];
    const sim = peer.adopt(ready.pipeId, ready.window, 64 * KiB);
    sim.send(frameItems([int(1), int(2)]));
    peer.sendState(ready.pipeId, PipeState.Ready | PipeState.ClientReaderServerWriterComplete);
    await until(() => { peer.poll(); return peer.states().at(-1)?.state === PipeState.Complete; });
    expect(got).toEqual([1, 2]);
    expect(sim.takeItems()).toEqual([int(2), int(4)]);
    peer.sendState(ready.pipeId, PipeState.Complete);
    await tick();
    expect(getPipeCounters().openPipes).toBe(0);
    await finish(h);
  });
});

describe('construction', () => {
  it('a channel over any runtime pipe; readers and writers need a runtime pipe', async () => {
    const h = await connectedHarness();
    const pipe = h.client.createPipe();
    const channel = new NexusDuplexChannel(pipe, intFormatter);
    expect(channel.pipe).toBe(pipe);
    expect(new NexusChannelReader(pipe, intFormatter).isComplete).toBe(false);
    expect(new NexusChannelWriter(pipe, intFormatter)).toBeInstanceOf(NexusChannelWriter);
    expect(() => new NexusDuplexChannel(pipe, /** @type {any} */ ({}))).toThrow(TypeError);
    await channel[Symbol.asyncDispose]();
    expect(getPipeCounters().openPipes).toBe(0);
    await finish(h);
  });
});
