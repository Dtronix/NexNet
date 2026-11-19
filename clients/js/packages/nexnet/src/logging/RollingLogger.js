/**
 * Logger that keeps the last lines in memory. Mirrors C# `RollingLogger`: tests dump it when they fail.
 * @module
 */

import { CoreLogger } from './CoreLogger.js';

/** @typedef {{ lines: string[], next: number, total: number }} RollingBuffer */

/** Keeps the last `maxLines` lines of the whole logger tree in one ring buffer. */
export class RollingLogger extends CoreLogger {
  /** @type {RollingBuffer} */
  #buffer;
  /** @type {number} */
  #maxLines;

  /**
   * @param {object} [options]
   * @param {number} [options.maxLines] Default 200.
   * @param {import('./levels.js').NexusMinLogLevel} [options.minLevel] Default `'trace'`.
   * @param {string | null} [options.pathSegment]
   * @param {() => number} [options.now]
   * @param {RollingLogger} [options.parent] Internal: use {@link RollingLogger#child}.
   */
  constructor(options = {}) {
    super(options);
    if (options.parent) {
      this.#buffer = options.parent.#buffer;
      this.#maxLines = options.parent.#maxLines;
    } else {
      const maxLines = options.maxLines ?? 200;
      if (!Number.isInteger(maxLines) || maxLines < 1) throw new RangeError('maxLines must be a positive integer');
      this.#maxLines = maxLines;
      this.#buffer = { lines: [], next: 0, total: 0 };
    }
  }

  /** Lines written since the last flush, including those rolled out of the buffer. @returns {number} */
  get totalLinesWritten() {
    return this.#buffer.total;
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} line
   */
  write(level, line) {
    const b = this.#buffer;
    b.lines[b.next] = line;
    b.next = (b.next + 1) % this.#maxLines;
    b.total++;
  }

  /** The buffered lines, oldest first. @returns {string[]} */
  lines() {
    const b = this.#buffer;
    if (b.total <= this.#maxLines) return b.lines.slice(0, b.total);
    return [...b.lines.slice(b.next), ...b.lines.slice(0, b.next)];
  }

  /**
   * Writes the buffered lines, oldest first, preceded by a truncation notice when lines were rolled out, then clears
   * the buffer (C# `Flush(TextWriter)`).
   * @param {(line: string) => void} writeLine
   * @returns {void}
   */
  flush(writeLine) {
    const b = this.#buffer;
    if (b.total === 0) return;
    if (b.total > this.#maxLines) {
      writeLine(`Truncating Log. Showing only last ${this.#maxLines} out of ${b.total} total lines written.`);
    }
    for (const line of this.lines()) writeLine(line);
    b.lines = [];
    b.next = 0;
    b.total = 0;
  }

  /** @param {string | null} pathSegment @returns {RollingLogger} */
  createChild(pathSegment) {
    return new RollingLogger({ parent: this, pathSegment });
  }

  /** @param {string} path @returns {RollingLogger} */
  child(path) {
    return this.createChild(path);
  }
}
