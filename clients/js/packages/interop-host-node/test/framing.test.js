import { describe, expect, it } from 'vitest';
import { FrameDecoder, encodeFrame, ControlProtocolError, MAX_FRAME_LENGTH } from '../src/core/framing.js';

/** @param {number} length @param {number} kind @param {Uint8Array} payload */
function raw(length, kind, payload) {
  const frame = new Uint8Array(5 + payload.length);
  new DataView(frame.buffer).setUint32(0, length, false);
  frame[4] = kind;
  frame.set(payload, 5);
  return frame;
}

describe('control framing', () => {
  it('writes a big-endian length, kind 0x01 and the UTF-8 JSON', () => {
    const frame = encodeFrame({ t: 'req', id: 1, s: 'é' });
    const json = new TextEncoder().encode(JSON.stringify({ t: 'req', id: 1, s: 'é' }));
    expect(new DataView(frame.buffer).getUint32(0, false)).toBe(json.length + 1);
    expect(frame[4]).toBe(0x01);
    expect(frame.subarray(5)).toEqual(json);
  });

  it('reads frames split across chunks and several frames in one chunk', () => {
    const a = encodeFrame({ t: 'a', n: 1 });
    const b = encodeFrame({ t: 'b', text: 'x'.repeat(1000) });
    const all = new Uint8Array(a.length + b.length);
    all.set(a, 0);
    all.set(b, a.length);
    const decoder = new FrameDecoder();
    const messages = [];
    for (let i = 0; i < all.length; i += 7) messages.push(...decoder.push(all.slice(i, i + 7)));
    expect(messages).toEqual([{ t: 'a', n: 1 }, { t: 'b', text: 'x'.repeat(1000) }]);
    expect(decoder.midFrame).toBe(false);

    const whole = new FrameDecoder().push(all);
    expect(whole.map((m) => m.t)).toEqual(['a', 'b']);
  });

  it('knows when the stream stops inside a frame', () => {
    const decoder = new FrameDecoder();
    decoder.push(encodeFrame({ t: 'a' }).subarray(0, 6));
    expect(decoder.midFrame).toBe(true);
  });

  it('refuses a zero or oversized length, a reserved kind and a payload that is not an object', () => {
    const json = new TextEncoder().encode('{}');
    expect(() => new FrameDecoder().push(raw(0, 1, new Uint8Array(0)))).toThrow(ControlProtocolError);
    expect(() => new FrameDecoder().push(raw(MAX_FRAME_LENGTH + 1, 1, json))).toThrow(ControlProtocolError);
    expect(() => new FrameDecoder().push(raw(json.length + 1, 2, json))).toThrow(ControlProtocolError);
    const array = new TextEncoder().encode('[1]');
    expect(() => new FrameDecoder().push(raw(array.length + 1, 1, array))).toThrow(ControlProtocolError);
    const bad = new Uint8Array([0xff, 0xfe]);
    expect(() => new FrameDecoder().push(raw(bad.length + 1, 1, bad))).toThrow(ControlProtocolError);
  });
});
