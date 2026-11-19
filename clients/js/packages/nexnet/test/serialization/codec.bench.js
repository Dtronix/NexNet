/**
 * Codec micro-benchmarks for the int64, string and ext 78 paths. Vitest benchmarks are not part of `npm test`; run
 * them in clients/js with `npx vitest bench --run codec.bench` (Node and Chromium).
 */

import { describe, test } from 'vitest';
import { MsgPackReader, MsgPackWriter, float64ArrayFormatter, int32ArrayFormatter, stringFormatter } from '../../src/serialization/index.js';

const writer = new MsgPackWriter({ initialCapacity: 1 << 20 });

/** @param {(w: MsgPackWriter) => void} write @returns {Uint8Array} */
function bytesOf(write) {
  writer.reset();
  write(writer);
  return writer.toUint8Array().slice();
}

const int64s = Array.from({ length: 1000 }, (_, i) => (BigInt(i) << 40n) * (i % 2 ? -1n : 1n));
const int64Bytes = bytesOf((w) => { for (const v of int64s) w.writeInt64(v); });

const shortStrings = Array.from({ length: 1000 }, (_, i) => `name-${i}`);
const longStrings = Array.from({ length: 100 }, (_, i) => `${'lorem ipsum dolor sit amet '.repeat(8)}${i}`);
const shortStringBytes = bytesOf((w) => { for (const s of shortStrings) stringFormatter.write(w, s); });
const longStringBytes = bytesOf((w) => { for (const s of longStrings) stringFormatter.write(w, s); });

const ints = Int32Array.from({ length: 100_000 }, (_, i) => i * 7);
const doubles = Float64Array.from({ length: 100_000 }, (_, i) => i / 3);
const intArrayBytes = bytesOf((w) => int32ArrayFormatter.write(w, ints));

describe('codec benchmarks', () => {
  test('int64', async ({ bench }) => {
    await bench.compare(
      bench('write 1000', () => {
        writer.reset();
        for (const v of int64s) writer.writeInt64(v);
      }),
      bench('read 1000', () => {
        const r = new MsgPackReader(int64Bytes);
        for (let i = 0; i < 1000; i++) r.readInt64();
      }),
    );
  });

  test('string', async ({ bench }) => {
    await bench.compare(
      bench('write 1000 short', () => {
        writer.reset();
        for (const s of shortStrings) stringFormatter.write(writer, s);
      }),
      bench('read 1000 short', () => {
        const r = new MsgPackReader(shortStringBytes);
        for (let i = 0; i < 1000; i++) r.readString();
      }),
      bench('write 100 long', () => {
        writer.reset();
        for (const s of longStrings) stringFormatter.write(writer, s);
      }),
      bench('read 100 long', () => {
        const r = new MsgPackReader(longStringBytes);
        for (let i = 0; i < 100; i++) r.readString();
      }),
    );
  });

  test('ext 78', async ({ bench }) => {
    await bench.compare(
      bench('write int[100000]', () => {
        writer.reset();
        int32ArrayFormatter.write(writer, ints);
      }),
      bench('write double[100000]', () => {
        writer.reset();
        float64ArrayFormatter.write(writer, doubles);
      }),
      bench('read int[100000]', () => {
        int32ArrayFormatter.read(new MsgPackReader(intArrayBytes));
      }),
    );
  });
});
