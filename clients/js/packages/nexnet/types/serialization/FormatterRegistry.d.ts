/**
 * The `Formatter<T>` contract and the formatter registry.
 * @module
 */
export type Formatter<T> = {
    /**
     * Writes `value`.
     */
    write: (writer: import('./MsgPackWriter.js').MsgPackWriter, value: T) => void;
    /**
     * Reads one value.
     */
    read: (reader: import('./MsgPackReader.js').MsgPackReader) => T;
};
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
 * Formatters by key. Generated code registers user target formatters (`[NexusTargetFormatter<T>]` mappings) and generic instantiations here; the
 * runtime looks up formatters it does not get passed directly.
 */
export declare class FormatterRegistry {
    #private;
    /**
     * Registers `formatter` under `key`. Throws when the key is already registered.
     * @template T
     * @param {string} key
     * @param {Formatter<T>} formatter
     * @returns {void}
     */
    register<T>(key: string, formatter: Formatter<T>): void;
    /**
     * Returns the formatter for `key`; throws when there is none.
     * @param {string} key
     * @returns {Formatter<any>}
     */
    get(key: string): Formatter<any>;
    /**
     * Returns the formatter for `key`, or undefined.
     * @param {string} key
     * @returns {Formatter<any> | undefined}
     */
    tryGet(key: string): Formatter<any> | undefined;
}
