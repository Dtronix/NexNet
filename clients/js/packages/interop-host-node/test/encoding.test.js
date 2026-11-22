import { createHash, randomBytes } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { Sha256, toBase64, fromBase64, pattern } from '../src/core/encoding.js';

describe('encoding helpers', () => {
  it('hashes like node:crypto for every length around the block boundaries, in pieces', () => {
    for (const length of [0, 1, 55, 56, 63, 64, 65, 119, 120, 127, 128, 1000, 100_003]) {
      const data = new Uint8Array(randomBytes(length));
      const expected = createHash('sha256').update(data).digest('hex');
      expect(new Sha256().update(data).hex()).toBe(expected);
      const pieces = new Sha256();
      for (let i = 0; i < data.length; i += 37) pieces.update(data.subarray(i, i + 37));
      expect(pieces.hex()).toBe(expected);
    }
  });

  it('round-trips base64', () => {
    const data = new Uint8Array(randomBytes(70_000));
    expect(toBase64(data)).toBe(Buffer.from(data).toString('base64'));
    expect(fromBase64(toBase64(data))).toEqual(data);
  });

  it('makes the pipe pattern (i * 31 + seed) mod 256 from any offset', () => {
    const whole = pattern(0, 1000, 7);
    for (let i = 0; i < whole.length; i++) expect(whole[i]).toBe((i * 31 + 7) % 256);
    expect(pattern(300, 200, 7)).toEqual(whole.subarray(300, 500));
  });
});
