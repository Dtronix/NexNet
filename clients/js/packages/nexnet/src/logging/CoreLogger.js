/**
 * Base of the formatting loggers: hierarchical paths, shared minimum level and behaviors, and the C# `CoreLogger`
 * line format `[seconds][LVL] [path(category)] message error`.
 * @module
 */

import { NexusLogBehaviors, NexusLogLevelTag, levelOrder } from './levels.js';

/** @typedef {import('../contract/config.js').NexusLogLevel} NexusLogLevel */
/** @typedef {import('./levels.js').NexusMinLogLevel} NexusMinLogLevel */

/**
 * State shared by a root logger and every logger created from it.
 * @typedef {object} LoggerRoot
 * @property {number} start Clock time the root was created.
 * @property {() => number} now
 * @property {number} minLevel
 * @property {number} behaviors
 * @property {boolean} enabled
 */

/**
 * One node of the logger path. A child keeps a reference to its parent node, so renaming a parent's segment renames
 * every descendant's path (C# `PathNode`).
 * @typedef {{ parent: PathNode | null, segment: string | null }} PathNode
 */

/**
 * Renders the `error` argument of a log call. Byte arrays are never written out (tokens are bytes): only their length.
 * @param {unknown} error
 * @returns {string}
 */
export function describeError(error) {
  if (error instanceof Uint8Array) return `<${error.constructor.name} ${error.byteLength} bytes>`;
  if (error instanceof ArrayBuffer) return `<ArrayBuffer ${error.byteLength} bytes>`;
  if (error instanceof Error) return error.stack ?? `${error.name}: ${error.message}`;
  try {
    return String(error);
  } catch {
    return '<unprintable>';
  }
}

/**
 * A {@link import('../contract/config.js').NexusLogger} that formats lines. Subclasses implement {@link CoreLogger#write}
 * and {@link CoreLogger#createChild}.
 *
 * @abstract
 */
export class CoreLogger {
  /** @type {LoggerRoot} */
  #root;
  /** @type {PathNode} */
  #node;

  /**
   * @param {object} [options]
   * @param {CoreLogger} [options.parent] The logger this one is created from; shares its level, behaviors and clock.
   * @param {string | null} [options.pathSegment]
   * @param {NexusMinLogLevel} [options.minLevel] Root only. Default `'trace'`.
   * @param {() => number} [options.now] Root only. Milliseconds; default `performance.now()`.
   */
  constructor({ parent, pathSegment = null, minLevel = 'trace', now } = {}) {
    if (parent) {
      this.#root = parent.#root;
      this.#node = { parent: parent.#node, segment: pathSegment };
    } else {
      const clock = now ?? (() => performance.now());
      this.#root = { start: clock(), now: clock, minLevel: levelOrder(minLevel), behaviors: NexusLogBehaviors.Default, enabled: true };
      this.#node = { parent: null, segment: pathSegment };
    }
  }

  /** Behavior flags ({@link NexusLogBehaviors}), shared by the whole logger tree. @returns {number} */
  get behaviors() {
    return this.#root.behaviors;
  }

  set behaviors(value) {
    this.#root.behaviors = value;
  }

  /** Lowest level written, shared by the whole logger tree. @returns {NexusMinLogLevel} */
  get minLevel() {
    const order = this.#root.minLevel;
    return /** @type {NexusMinLogLevel} */ (Object.keys(NexusLogLevelTag)[order]);
  }

  set minLevel(level) {
    this.#root.minLevel = levelOrder(level);
  }

  /** False turns the whole logger tree off. @returns {boolean} */
  get enabled() {
    return this.#root.enabled;
  }

  set enabled(value) {
    this.#root.enabled = value;
  }

  /** This logger's own path segment; may be renamed. @returns {string | null} */
  get pathSegment() {
    return this.#node.segment;
  }

  set pathSegment(value) {
    this.#node.segment = value;
  }

  /** The non-blank segments from the root to this logger, joined with `|`. @returns {string} */
  get formattedPath() {
    /** @type {string[]} */
    const segments = [];
    for (let node = /** @type {PathNode | null} */ (this.#node); node !== null; node = node.parent) {
      if (node.segment !== null && node.segment.trim() !== '') segments.push(node.segment);
    }
    return segments.reverse().join('|');
  }

  /**
   * True when a message of `level` would be written.
   * @param {NexusLogLevel} level
   * @returns {boolean}
   */
  isEnabled(level) {
    return this.#root.enabled && levelOrder(level) >= this.#root.minLevel && this.#root.minLevel < 6;
  }

  /**
   * @param {NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   * @returns {void}
   */
  log(level, message, error) {
    const line = this.format(level, message, error);
    if (line !== null) this.write(level, line);
  }

  /**
   * Creates a logger for a sub-path (`'Session 3'`, `'Pipe 7'`).
   * @param {string} path
   * @returns {CoreLogger}
   */
  child(path) {
    return this.createChild(path);
  }

  /**
   * Formats a line as C# `CoreLogger` does, or returns null when the level is filtered out.
   * @param {NexusLogLevel} level
   * @param {string} message
   * @param {unknown} [error]
   * @param {string | null} [category]
   * @returns {string | null}
   */
  format(level, message, error, category = null) {
    if (!this.isEnabled(level)) return null;
    const seconds = ((this.#root.now() - this.#root.start) / 1000).toFixed(6);
    const tag = NexusLogLevelTag[level];
    let line = `[${seconds}][${tag}] [${this.formattedPath}${category === null ? '' : `(${category})`}] ${message}`;
    if (error !== undefined && error !== null) line += ` ${describeError(error)}`;
    return line;
  }

  /**
   * Writes a formatted line.
   * @abstract
   * @param {NexusLogLevel} level
   * @param {string} line
   * @returns {void}
   */
  write(level, line) {
    throw new Error('CoreLogger.write is abstract');
  }

  /**
   * @abstract
   * @param {string | null} pathSegment
   * @returns {CoreLogger}
   */
  createChild(pathSegment) {
    throw new Error('CoreLogger.createChild is abstract');
  }
}
