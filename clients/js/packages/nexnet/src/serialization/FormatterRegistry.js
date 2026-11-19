/**
 * The `Formatter<T>` contract and the formatter registry.
 * @module
 */

/**
 * Reads and writes one value of type `T`. Generated code emits one formatter per type; the runtime ships the
 * built-in ones (`./formatters.js`). A formatter is a plain object; it holds no per-call state.
 *
 * - `write` writes exactly one MessagePack value. It throws `TypeError` for a value of the wrong JS type and
 *   `RangeError` for one outside the C# type's range.
 * - `read` reads exactly one value. It throws {@link import('./errors.js').NexusSerializationError} for input it
 *   refuses. A formatter of a container (sequence, set, map, tuple, object, union) calls `reader.enter()` before its
 *   array or map header and `reader.exit()` after its last element.
 *
 * @template T
 * @typedef {object} Formatter
 * @property {(writer: import('./MsgPackWriter.js').MsgPackWriter, value: T) => void} write Writes `value`.
 * @property {(reader: import('./MsgPackReader.js').MsgPackReader) => T} read Reads one value.
 */

/**
 * Formatters by key. Generated code registers user target formatters (D12) and generic instantiations here; the
 * runtime looks up formatters it does not get passed directly.
 */
export class FormatterRegistry {
  /** @type {Map<string, Formatter<any>>} */
  #formatters = new Map();

  /**
   * Registers `formatter` under `key`. Throws when the key is already registered.
   * @template T
   * @param {string} key
   * @param {Formatter<T>} formatter
   * @returns {void}
   */
  register(key, formatter) {
    if (typeof key !== 'string') throw new TypeError('a formatter key is a string');
    if (formatter === null || typeof formatter !== 'object' || typeof formatter.write !== 'function' || typeof formatter.read !== 'function') {
      throw new TypeError(`the formatter for '${key}' must have write and read functions`);
    }
    if (this.#formatters.has(key)) throw new Error(`a formatter is already registered for '${key}'`);
    this.#formatters.set(key, formatter);
  }

  /**
   * Returns the formatter for `key`; throws when there is none.
   * @param {string} key
   * @returns {Formatter<any>}
   */
  get(key) {
    const formatter = this.#formatters.get(key);
    if (formatter === undefined) throw new Error(`no formatter is registered for '${key}'`);
    return formatter;
  }

  /**
   * Returns the formatter for `key`, or undefined.
   * @param {string} key
   * @returns {Formatter<any> | undefined}
   */
  tryGet(key) {
    return this.#formatters.get(key);
  }
}
