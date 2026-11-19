import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { setInvocationCodec } from '../../src/invocation/codec.js';
import { ListMessageTag, arrayFormatter, listMessageFormatter } from '../../src/collections/messages.js';
import {
  collectionTestCodec, fromHex, int32ArrayFormatter, intFormatter, loadVectors, stringFormatter, toHex,
} from './support/codec.js';

/** @type {import('../../src/invocation/codec.js').InvocationCodec} */
let previousCodec;
beforeEach(() => {
  previousCodec = setInvocationCodec(collectionTestCodec);
});
afterEach(() => {
  setInvocationCodec(previousCodec);
});

const vectors = /** @type {import('./support/codec.js').MessageVector[]} */ (loadVectors().filter((v) => v.kind === 'message'));
const ints = listMessageFormatter(intFormatter, int32ArrayFormatter);
const strings = listMessageFormatter(stringFormatter);

/** @param {import('./support/codec.js').MessageVector} v */
const usesStrings = (v) => 'strings' in v.fields || 'string' in v.fields;

/**
 * The fields of a vector as the decoded message carries them.
 * @param {Record<string, unknown>} fields
 */
function expected(fields) {
  const { strings: s, string, ...rest } = fields;
  if (s !== undefined) return { ...rest, values: s };
  if (string !== undefined) return { ...rest, value: string };
  return rest;
}

/** @param {import('../../src/contract/codec.js').Formatter<any>} formatter @param {Uint8Array} bytes */
function decode(formatter, bytes) {
  const reader = collectionTestCodec.createReader(bytes);
  const value = formatter.read(reader);
  expect(reader.remaining).toBe(0);
  return value;
}

/** @param {import('../../src/contract/codec.js').Formatter<any>} formatter @param {unknown} value */
function encode(formatter, value) {
  const writer = collectionTestCodec.createWriter();
  formatter.write(writer, value);
  return toHex(writer.toUint8Array());
}

describe('C# list items (fixtures/collections.jsonl)', () => {
  it('has a vector for every tag', () => {
    expect(new Set(vectors.map((v) => v.fields.tag))).toEqual(new Set(Object.values(ListMessageTag)));
  });

  for (const v of vectors) {
    it(`decodes ${v.name}`, () => {
      const formatter = usesStrings(v) ? strings : ints;
      expect(decode(formatter, fromHex(v.hex))).toEqual(expected(v.fields));
    });

    // C# writes int[] as ext 78; the JS client never writes ResetValues, so only the other items are re-encoded.
    if (v.fields.tag !== ListMessageTag.ResetValues || usesStrings(v)) {
      it(`encodes ${v.name} byte for byte`, () => {
        const formatter = usesStrings(v) ? strings : ints;
        expect(encode(formatter, expected(v.fields))).toBe(v.hex);
      });
    }
  }
});

describe('list item codec', () => {
  it('rejects an unknown tag, a wrong field count, a non-union value and out-of-range fields', () => {
    const bad = [
      '92099100', // tag 9
      '9200920000', // ResetStart with 2 fields
      '9300000000', // a 3-element array
      '9204940001ce8000000000', // Insert with a version beyond int32
      '920893cd0100', // Noop with 3 fields
      '920891cd0100', // flags beyond uint8
    ];
    for (const hex of bad) expect(() => decode(ints, fromHex(hex)), hex).toThrow();
  });

  it('writes nil for null reset values and reads them back as null', () => {
    const plain = listMessageFormatter(intFormatter);
    const hex = encode(plain, { tag: ListMessageTag.ResetValues, flags: 0, values: null });
    expect(hex).toBe('92019200c0');
    expect(decode(plain, fromHex(hex))).toEqual({ tag: 1, flags: 0, values: null });
    expect(encode(arrayFormatter(intFormatter), [1, 2])).toBe('920102');
  });

  it('refuses to write an unknown tag', () => {
    expect(() => encode(ints, { tag: 12, flags: 0 })).toThrow(RangeError);
  });
});
