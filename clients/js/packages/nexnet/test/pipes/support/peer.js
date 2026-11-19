/**
 * Plays the server side of pipes against a client over the invocation harness (a LoopbackTransport): parses what the
 * client wrote, frame by frame, and sends pipe frames back.
 * @module
 */

import { encodeInvocation, InvocationFlags } from '../../../src/invocation/messages.js';
import { FrameReader } from '../../../src/session/FrameReader.js';
import { encodeFrame, encodePipeFrame, encodePipeWindowUpdate } from '../../../src/session/frames.js';
import { MessageType } from '../../../src/session/protocol.js';
import { decodeDuplexPipeUpdateState, encodeDuplexPipeUpdateState } from '../../../src/session/wire.js';
import { PipeState } from '../../../src/pipes/constants.js';
import { prefixLength, readLength, writeLength } from '../../../src/pipes/framing.js';
import { concat } from '../../session/support/LoopbackTransport.js';
import { pack } from '../../invocation/support/testCodec.js';

/**
 * @typedef {object} PeerFrame
 * @property {number} type
 * @property {Uint8Array} body
 * @property {number} pipeId
 */

/**
 * @typedef {Awaited<ReturnType<typeof import('../../invocation/support/harness.js').connectedHarness>>} Harness
 */

/** Calls a protected proxy member. @param {Harness} h @returns {any} */
export const proxyOf = (h) => /** @type {any} */ (h.proxy);

/** Pipe method arguments `[pipeId]`. @type {(w: import('../../../src/contract/codec.js').MsgPackWriter, pipeId: number) => void} */
export const pipeArgs = (w, pipeId) => {
  w.writeArrayHeader(1);
  w.writeInt(pipeId);
};

/** The `DuplexPipeWrite` frames' payload, concatenated. @param {PeerFrame[]} frames */
export const payload = (frames) => concat(frames.filter((f) => f.type === MessageType.DuplexPipeWrite).map((f) => f.body));

export class ServerPeer {
  /** @type {Harness} */
  h;
  #reader = new FrameReader();
  #writesSeen = 0;
  /** Every frame the client wrote (greetings and pings excluded). @type {PeerFrame[]} */
  frames = [];
  /** @type {Map<number, SimPipe>} */
  pipes = new Map();
  /** Disconnect byte the client sent, or 0. */
  disconnect = 0;

  /** @param {Harness} h */
  constructor(h) {
    this.h = h;
    this.#reader.handshakeComplete = true;
  }

  /** Parses what the client wrote since the last poll; routes pipe frames to the simulated pipes. @returns {PeerFrame[]} */
  poll() {
    /** @type {PeerFrame[]} */
    const fresh = [];
    const writes = this.h.transport.writes;
    while (this.#writesSeen < writes.length) {
      const result = this.#reader.push(writes[this.#writesSeen++], (f) => {
        if (f.type === MessageType.ClientGreeting || f.type === MessageType.ClientGreetingReconnection || f.type === MessageType.Ping) return true;
        fresh.push({ type: f.type, body: f.body.slice(), pipeId: f.pipeId });
        return true;
      });
      if (result.kind === 'disconnect') this.disconnect = result.reason;
    }
    for (const frame of fresh) {
      this.frames.push(frame);
      if (frame.type === MessageType.DuplexPipeWrite || frame.type === MessageType.PipeWindowUpdate) {
        this.pipes.get(frame.pipeId)?.onFrame(frame);
      } else if (frame.type === MessageType.DuplexPipeUpdateState) {
        const state = decodeDuplexPipeUpdateState(frame.body);
        this.pipes.get(state.pipeId)?.onState(state.state, state.window);
      }
    }
    return fresh;
  }

  /** Frames of one type written so far. @param {number} type @returns {PeerFrame[]} */
  framesOf(type) {
    this.poll();
    return this.frames.filter((f) => f.type === type);
  }

  /** Decoded `DuplexPipeUpdateState` messages written so far. */
  states() {
    return this.framesOf(MessageType.DuplexPipeUpdateState).map((f) => decodeDuplexPipeUpdateState(f.body));
  }

  /** @param {Uint8Array} bytes */
  send(bytes) {
    this.h.send(bytes);
  }

  /** @param {number} pipeId @param {number} state @param {number} [window] */
  sendState(pipeId, state, window = 0) {
    this.send(encodeFrame(MessageType.DuplexPipeUpdateState, encodeDuplexPipeUpdateState(pipeId, state, window)));
  }

  /** @param {number} pipeId @param {number} credit */
  sendWindowUpdate(pipeId, credit) {
    this.send(encodePipeWindowUpdate(pipeId, credit));
  }

  /** @param {number} pipeId @param {Uint8Array} bytes */
  sendData(pipeId, bytes) {
    this.send(encodePipeFrame(MessageType.DuplexPipeWrite, pipeId, bytes));
  }

  /**
   * Invokes a client method with a pipe parameter: `IgnoreReturn | DuplexPipe`, invocation ID 0, args `[serverId]`.
   * @param {number} methodId @param {number} serverId
   */
  invokePipeMethod(methodId, serverId) {
    const args = pack((w) => { w.writeArrayHeader(1); w.writeInt(serverId); });
    this.send(encodeFrame(MessageType.Invocation, encodeInvocation(0, methodId, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, args)));
  }

  /**
   * Opening side's pipe answered with Ready: the server allocates `serverId` and grants `window`.
   * @param {number} clientId @param {number} serverId @param {number} window
   * @returns {SimPipe}
   */
  accept(clientId, serverId, window) {
    const id = clientId | (serverId << 8);
    const pipe = new SimPipe(this, id, window);
    this.pipes.set(id, pipe);
    this.sendState(id, PipeState.Ready, window);
    return pipe;
  }

  /**
   * A pipe the server opened and the client registered (the client's Ready carried the client's window).
   * @param {number} fullId @param {number} clientWindow @param {number} serverWindow
   * @returns {SimPipe}
   */
  adopt(fullId, clientWindow, serverWindow) {
    const pipe = new SimPipe(this, fullId, serverWindow);
    pipe.sendCredit = clientWindow;
    this.pipes.set(fullId, pipe);
    this.sendWindowUpdate(fullId, serverWindow);
    return pipe;
  }
}

/**
 * The server's end of one pipe, honoring the flow-control rules: never sends beyond the credit the client granted,
 * fails on data beyond its own credit, and grants back half its window at a time as it consumes.
 */
export class SimPipe {
  /** @type {ServerPeer} */
  peer;
  id;
  window;
  /** Credit the client granted the server. */
  sendCredit = 0;
  /** Credit the server granted the client and the client has not used. */
  recvCredit;
  consumedSinceGrant = 0;
  /** @type {Uint8Array[]} */
  #chunks = [];
  buffered = 0;
  /** Everything consumed, in order. @type {Uint8Array[]} */
  consumed = [];
  /** Bytes consumed so far. */
  consumedTotal = 0;
  /** @type {number[]} */
  statesReceived = [];
  /** @type {string[]} */
  violations = [];

  /** @param {ServerPeer} peer @param {number} id @param {number} window */
  constructor(peer, id, window) {
    this.peer = peer;
    this.id = id;
    this.window = window;
    this.recvCredit = window;
  }

  /** @param {PeerFrame} frame */
  onFrame(frame) {
    if (frame.type === MessageType.PipeWindowUpdate) {
      this.sendCredit += new DataView(frame.body.buffer, frame.body.byteOffset, 4).getUint32(0, true);
      return;
    }
    if (frame.body.length > this.recvCredit) this.violations.push(`data ${frame.body.length} beyond credit ${this.recvCredit}`);
    this.recvCredit -= frame.body.length;
    this.#chunks.push(frame.body);
    this.buffered += frame.body.length;
  }

  /** @param {number} state @param {number} window */
  onState(state, window) {
    this.statesReceived.push(state);
    if (window > 0) this.sendCredit += window;
  }

  /**
   * Consumes up to `n` buffered bytes and grants credit back at half the window.
   * @param {number} n
   * @returns {Uint8Array}
   */
  consume(n) {
    const out = this.#take(Math.min(n, this.buffered));
    this.#grant(out.length);
    return out;
  }

  /** @param {number} n */
  #take(n) {
    const out = new Uint8Array(n);
    let written = 0;
    while (written < n) {
      const first = this.#chunks[0];
      const take = Math.min(first.length, n - written);
      out.set(first.subarray(0, take), written);
      written += take;
      if (take === first.length) this.#chunks.shift();
      else this.#chunks[0] = first.subarray(take);
    }
    this.buffered -= n;
    this.consumedTotal += n;
    if (n > 0) this.consumed.push(out);
    return out;
  }

  /** @param {number} n */
  #grant(n) {
    this.consumedSinceGrant += n;
    if (this.consumedSinceGrant >= this.window / 2) this.grantNow();
  }

  /** Grants everything consumed and not yet granted back. */
  grantNow() {
    if (this.consumedSinceGrant === 0) return;
    this.recvCredit += this.consumedSinceGrant;
    this.peer.sendWindowUpdate(this.id, this.consumedSinceGrant);
    this.consumedSinceGrant = 0;
  }

  /**
   * Sends as much of `bytes` as the credit allows, in frames of at most `maxFrame` bytes. Returns the bytes sent.
   * @param {Uint8Array} bytes @param {number} [maxFrame]
   * @returns {number}
   */
  send(bytes, maxFrame = 0xffff) {
    let offset = 0;
    while (offset < bytes.length && this.sendCredit > 0) {
      const n = Math.min(bytes.length - offset, this.sendCredit, maxFrame);
      this.peer.sendData(this.id, bytes.subarray(offset, offset + n));
      this.sendCredit -= n;
      offset += n;
    }
    return offset;
  }

  /** Everything consumed, concatenated. @returns {Uint8Array} */
  get received() {
    return concat(this.consumed);
  }

  /**
   * Channel items fully buffered, decoded as raw item bytes (consumes them).
   * @returns {Uint8Array[]}
   */
  takeItems() {
    /** @type {Uint8Array[]} */
    const items = [];
    for (;;) {
      const head = this.#peek(5);
      const prefix = readLength(head);
      if (prefix === null || this.buffered < prefix.size + prefix.length) break;
      const bytes = this.consume(prefix.size + prefix.length);
      items.push(bytes.subarray(prefix.size));
    }
    return items;
  }

  /** @param {number} n */
  #peek(n) {
    return concat(this.#chunks).subarray(0, Math.min(n, this.buffered));
  }
}

/**
 * Frames items with their length prefixes.
 * @param {Uint8Array[]} items
 * @returns {Uint8Array}
 */
export function frameItems(items) {
  let total = 0;
  for (const item of items) total += prefixLength(item.length) + item.length;
  const out = new Uint8Array(total);
  let offset = 0;
  for (const item of items) {
    offset += writeLength(out, offset, item.length);
    out.set(item, offset);
    offset += item.length;
  }
  return out;
}

/**
 * A seeded PRNG (mulberry32), so a failing property case can be replayed.
 * @param {number} seed
 * @returns {{ next(): number, int(min: number, max: number): number }}
 */
export function random(seed) {
  let a = seed >>> 0;
  const next = () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  return { next, int: (min, max) => min + Math.floor(next() * (max - min + 1)) };
}

/**
 * Random bytes of length `n` from `rng`.
 * @param {{ int(min: number, max: number): number }} rng @param {number} n
 * @returns {Uint8Array}
 */
export function bytesOf(rng, n) {
  const out = new Uint8Array(n);
  for (let i = 0; i < n; i++) out[i] = rng.int(0, 255);
  return out;
}
