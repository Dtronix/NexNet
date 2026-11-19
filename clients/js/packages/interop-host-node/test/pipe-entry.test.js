import { describe, expect, it, vi } from 'vitest';

// A stand-in channel that records its formatter, so the test needs no connected pipe.
vi.mock('@dtronix/nexnet', async (importOriginal) => {
  const original = /** @type {Record<string, unknown>} */ (await importOriginal());
  return {
    ...original,
    NexusDuplexChannel: class {
      /**
       * @param {unknown} pipe
       * @param {unknown} formatter
       */
      constructor(pipe, formatter) {
        this.pipe = pipe;
        this.formatter = formatter;
      }
    },
  };
});

const { PipeEntry } = await import('../src/core/host.js');

/**
 * @param {string} expression
 * @returns {import('../src/core/types.js').TypedCodec}
 */
function codecOf(expression) {
  return { expression, formatter: /** @type {any} */ ({ name: expression }), toJson: (v) => v, fromJson: (v) => v };
}

describe('PipeEntry', () => {
  it('gives each item type its own channel view, so a pipe can read one type and write another', () => {
    const pipe = { completed: new Promise(() => {}) };
    const entry = new PipeEntry('p1', /** @type {any} */ (null), /** @type {any} */ (null), pipe);
    const ints = /** @type {any} */ (entry.channelFor(codecOf('int')));
    const strings = /** @type {any} */ (entry.channelFor(codecOf('string')));
    expect(ints.formatter).toEqual({ name: 'int' });
    expect(strings.formatter).toEqual({ name: 'string' });
    expect(ints.pipe).toBe(pipe);
    expect(strings.pipe).toBe(pipe);
    // The same type gets the same view (its reader and writer are created once).
    expect(entry.channelFor(codecOf('int'))).toBe(ints);
    expect(entry.channelFor(codecOf('string'))).toBe(strings);
  });
});
