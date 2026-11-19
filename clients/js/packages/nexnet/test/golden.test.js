/**
 * The golden vectors (`tests/golden`, located from the repo root) replayed against the JS codec in Node.
 * The same suite runs in Chromium: `test/serialization/golden.browser.test.js`.
 */

import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { GoldenCodecs, VECTOR_FILES, parseVectors, replay } from './serialization/support/golden.js';

const dir = new URL('../../../../../tests/golden/', import.meta.url);

/** @param {string} name @returns {string} */
const read = (name) => readFileSync(new URL(name, dir), 'utf8');

const codecs = new GoldenCodecs(read('schemas.jsonl'));
const vectors = VECTOR_FILES.flatMap((file) => parseVectors(file, read(`${file}.jsonl`)));

describe('golden vectors (Node)', () => {
  it('every file has vectors and ids are unique', () => {
    for (const file of VECTOR_FILES) expect(vectors.some((v) => v.file === file), file).toBe(true);
    expect(new Set(vectors.map((v) => v.id)).size).toBe(vectors.length);
    expect(vectors.filter((v) => v.mode === 'reject').length).toBe(vectors.filter((v) => v.file === 'reject').length);
  });

  it.each(vectors.map((v) => /** @type {[string, import('./serialization/support/golden.js').Vector]} */ ([`${v.file}: ${v.id}`, v])))('%s', (_, vector) => {
    replay(vector, codecs);
  });
});
