import { describe, expect, it } from 'vitest';
import { DisconnectReason } from '../../src/contract/enums.js';
import { FrameReader } from '../../src/session/FrameReader.js';
import {
  encodeChunkFrame, encodeFrame, encodeHeaderOnly, encodePipeFrame, encodePipeWindowUpdate, encodePreamble,
} from '../../src/session/frames.js';
import { MessageType } from '../../src/session/protocol.js';
import { fromHex, recordings } from './support/fixtures.js';

/**
 * Pushes `chunks` and collects frames (bodies copied).
 * @param {FrameReader} reader
 * @param {Uint8Array[]} chunks
 */
function collect(reader, chunks) {
  /** @type {Array<{ type: number, body: number[], pipeId: number, invocationId: number, kind: number }>} */
  const frames = [];
  /** @type {import('../../src/session/FrameReader.js').PushResult[]} */
  const results = [];
  for (const chunk of chunks) {
    const result = reader.push(chunk, (f) => {
      frames.push({ type: f.type, body: [...f.body], pipeId: f.pipeId, invocationId: f.invocationId, kind: f.kind });
      return true;
    });
    results.push(result);
    if (result.kind !== 'more') break;
  }
  return { frames, last: results[results.length - 1] };
}

/** @param {Uint8Array} bytes @param {number} size @returns {Uint8Array[]} */
function split(bytes, size) {
  const out = [];
  for (let i = 0; i < bytes.length; i += size) out.push(bytes.subarray(i, i + size));
  return out;
}

const sample = (() => {
  const parts = [
    encodePreamble(),
    encodeFrame(MessageType.ServerGreeting, Uint8Array.of(0x94, 1, 2, 3, 4)),
    encodeHeaderOnly(MessageType.Ping),
    encodePipeFrame(MessageType.DuplexPipeWrite, 0x0201, Uint8Array.of(9, 8, 7)),
    encodePipeWindowUpdate(0x0102, 65536),
    encodeChunkFrame(0x1234, 1, new Uint8Array(300).fill(5)),
    encodeFrame(MessageType.InvocationCredit, Uint8Array.of(0x91, 0x10)),
  ];
  const all = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { all.set(p, o); o += p.length; }
  return all;
})();

describe('FrameReader', () => {
  it('parses every frame layout with the post-headers little-endian', () => {
    const reader = new FrameReader();
    reader.handshakeComplete = true;
    const { frames, last } = collect(reader, [sample]);
    expect(last.kind).toBe('more');
    expect(frames.map((f) => f.type)).toEqual([105, 1, 50, 51, 114, 116]);
    expect(frames[0].body).toEqual([0x94, 1, 2, 3, 4]);
    expect(frames[1].body).toEqual([]);
    expect(frames[2]).toMatchObject({ pipeId: 0x0201, body: [9, 8, 7] });
    expect(frames[3]).toMatchObject({ pipeId: 0x0102, body: [0, 0, 1, 0] });
    expect(frames[4]).toMatchObject({ invocationId: 0x1234, kind: 1 });
    expect(frames[4].body.length).toBe(300);
    expect(reader.buffered).toBe(0);
  });

  for (const size of [1, 2, 3, 7, 64]) {
    it(`reassembles frames split into ${size}-byte chunks`, () => {
      const whole = collect(Object.assign(new FrameReader(), { handshakeComplete: true }), [sample]).frames;
      const reader = new FrameReader();
      reader.handshakeComplete = true;
      expect(collect(reader, split(sample, size)).frames).toEqual(whole);
      expect(reader.buffered).toBe(0);
    });
  }

  it('replays every recorded C# server stream at every split point', () => {
    for (const rec of Object.values(recordings)) {
      const bytes = fromHex(rec.serverToClient);
      const expected = collect(new FrameReader(), [bytes]);
      for (let cut = 1; cut < bytes.length; cut++) {
        const got = collect(new FrameReader(), [bytes.subarray(0, cut), bytes.subarray(cut)]);
        expect(got.frames, `${rec.scenario} cut ${cut}`).toEqual(expected.frames);
        expect(got.last, `${rec.scenario} cut ${cut}`).toEqual(expected.last);
      }
    }
  });

  it('reports a recorded server disconnect byte with its reason', () => {
    const { last } = collect(new FrameReader(), [fromHex(recordings['client-mismatch'].serverToClient)]);
    expect(last).toEqual({ kind: 'disconnect', reason: DisconnectReason.ClientMismatch });
  });

  it('reads every byte in 20-38 as a disconnect, assigned or not, before anything else', () => {
    for (let code = 20; code <= 38; code++) {
      const { frames, last } = collect(new FrameReader(), [Uint8Array.of(...encodePreamble(), 1, code, 1, 1)]);
      expect(frames.map((f) => f.type)).toEqual([1]);
      expect(last).toEqual({ kind: 'disconnect', reason: code });
    }
  });

  it('rejects a bad preamble', () => {
    const cases = [
      [0x4e, 0x6e, 0x50, 0x15, 0, 0, 0, 1],
      [0x4e, 0x6e, 0x50, 0x14, 0, 1, 0, 1],
      [0x4e, 0x6e, 0x50, 0x14, 0, 0, 0, 2],
    ];
    for (const preamble of cases) {
      expect(collect(new FrameReader(), [Uint8Array.from(preamble)]).last).toMatchObject({ kind: 'error', reason: DisconnectReason.ProtocolError });
    }
    expect(collect(new FrameReader(), [Uint8Array.of(0x4e, 0x6e, 0x50)]).last.kind).toBe('more');
  });

  it('rejects unknown message types', () => {
    for (const type of [0, 2, 19, 39, 49, 52, 99, 102, 106, 117, 121, 255]) {
      expect(collect(new FrameReader(), [Uint8Array.of(...encodePreamble(), type)]).last, String(type))
        .toMatchObject({ kind: 'error', reason: DisconnectReason.ProtocolError });
    }
  });

  it('rejects a body over 16 KiB before the connection completes, as soon as its length is read', () => {
    const reader = new FrameReader();
    const header = Uint8Array.of(...encodePreamble(), MessageType.ServerGreeting, 0x01, 0x40); // 16385
    expect(collect(reader, [header]).last).toMatchObject({ kind: 'error', reason: DisconnectReason.ProtocolError });

    const exact = new FrameReader();
    expect(collect(exact, [Uint8Array.of(...encodePreamble(), MessageType.ServerGreeting, 0x00, 0x40)]).last.kind).toBe('more');

    const after = new FrameReader();
    after.handshakeComplete = true;
    expect(collect(after, [Uint8Array.of(...encodePreamble(), MessageType.Invocation, 0xff, 0xff)]).last.kind).toBe('more');
  });

  it('stops when the sink returns false', () => {
    const reader = new FrameReader();
    reader.handshakeComplete = true;
    let seen = 0;
    const result = reader.push(sample, () => ++seen < 2);
    expect(result.kind).toBe('stopped');
    expect(seen).toBe(2);
  });

  it('refuses frame bodies over 65,535 bytes when encoding', () => {
    expect(() => encodeFrame(MessageType.Invocation, new Uint8Array(65536))).toThrow(RangeError);
    expect(encodeFrame(MessageType.Invocation, new Uint8Array(65535)).length).toBe(65538);
  });
});
