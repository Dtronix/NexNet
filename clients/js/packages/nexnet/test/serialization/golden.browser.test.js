/// <reference types="vite/client" />
/**
 * The golden vectors replayed against the JS codec in Chromium (D28/D29). The files are bundled with
 * `import.meta.glob`; the Node suite is `test/golden.test.js`.
 */

import { describe, expect, it } from 'vitest';
import { GoldenCodecs, VECTOR_FILES, parseVectors, replay } from './support/golden.js';

const files = /** @type {Record<string, string>} */ (import.meta.glob('../../../../../../tests/golden/*.jsonl', { query: '?raw', import: 'default', eager: true }));

/** @param {string} name @returns {string} */
function read(name) {
  const key = Object.keys(files).find((k) => k.endsWith(`/${name}`));
  if (key === undefined) throw new Error(`tests/golden/${name} is not bundled`);
  return files[key];
}

const codecs = new GoldenCodecs(read('schemas.jsonl'));
const vectors = VECTOR_FILES.flatMap((file) => parseVectors(file, read(`${file}.jsonl`)));

describe('golden vectors (browser)', () => {
  it('every file is bundled', () => {
    for (const file of VECTOR_FILES) expect(vectors.some((v) => v.file === file), file).toBe(true);
  });

  it.each(vectors.map((v) => /** @type {[string, import('./support/golden.js').Vector]} */ ([`${v.file}: ${v.id}`, v])))('%s', (_, vector) => {
    replay(vector, codecs);
  });
});
