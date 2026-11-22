/**
 * Logger that writes to the console. Mirrors C# `ConsoleLogger`.
 * @module
 */

import { CoreLogger } from './CoreLogger.js';

/**
 * @typedef {object} ConsoleLike
 * @property {(line: string) => void} debug
 * @property {(line: string) => void} info
 * @property {(line: string) => void} warn
 * @property {(line: string) => void} error
 */

/** Writes each line to `console.debug`, `info`, `warn` or `error` by level. */
export class ConsoleLogger extends CoreLogger {
  /** @type {ConsoleLike} */
  #console;

  /**
   * @param {object} [options]
   * @param {import('./levels.js').NexusMinLogLevel} [options.minLevel] Default `'trace'`.
   * @param {string | null} [options.pathSegment]
   * @param {ConsoleLike} [options.console] Default: the global `console`.
   * @param {() => number} [options.now]
   * @param {ConsoleLogger} [options.parent] Internal: use {@link ConsoleLogger#child}.
   */
  constructor(options = {}) {
    super(options);
    this.#console = options.console ?? (options.parent ? options.parent.#console : globalThis.console);
  }

  /**
   * @param {import('../contract/config.js').NexusLogLevel} level
   * @param {string} line
   */
  write(level, line) {
    const c = this.#console;
    switch (level) {
      case 'trace':
      case 'debug':
        c.debug(line);
        break;
      case 'information':
        c.info(line);
        break;
      case 'warning':
        c.warn(line);
        break;
      default:
        c.error(line);
        break;
    }
  }

  /** @param {string | null} pathSegment @returns {ConsoleLogger} */
  createChild(pathSegment) {
    return new ConsoleLogger({ parent: this, pathSegment });
  }

  /** @param {string} path @returns {ConsoleLogger} */
  child(path) {
    return this.createChild(path);
  }
}
