/**
 * Ext 78 primitive arrays (protocol specification §10.5): one element-kind byte, then the elements' raw little-endian
 * bytes. JS uses typed arrays; a read copies the data into a new, aligned buffer.
 * @module
 */
/** The ext type of a primitive array. */
export declare const PRIMITIVE_ARRAY_EXT = 78;
export type PrimitiveTypedArray = Int8Array | Int16Array | Uint16Array | Int32Array | Uint32Array | BigInt64Array | BigUint64Array | Float32Array | Float64Array;
export type ElementKind = {
    /**
     * The kind byte.
     */
    kind: number;
    /**
     * Bytes per element on the wire.
     */
    size: number;
    /**
     * C# element type.
     */
    name: string;
    /**
     * The typed-array constructor JS uses.
     */
    array: any;
    /**
     * Elements are binary16 on the wire, float32 in JS.
     */
    half?: boolean;
    /**
     * Elements are bigints.
     */
    bigint?: boolean;
};
/**
 * @typedef {Int8Array | Int16Array | Uint16Array | Int32Array | Uint32Array | BigInt64Array | BigUint64Array | Float32Array | Float64Array} PrimitiveTypedArray
 */
/**
 * One element kind.
 * @typedef {object} ElementKind
 * @property {number} kind The kind byte.
 * @property {number} size Bytes per element on the wire.
 * @property {string} name C# element type.
 * @property {any} array The typed-array constructor JS uses.
 * @property {boolean} [half] Elements are binary16 on the wire, float32 in JS.
 * @property {boolean} [bigint] Elements are bigints.
 */
/** The element kinds, by C# element type (§10.5). @type {Readonly<Record<string, Readonly<ElementKind>>>} */
export declare const ElementKinds: Readonly<Record<string, Readonly<ElementKind>>>;
/**
 * Writes `value` as an ext 78 array of `element`, or nil for null. `value` is the element's typed array or any
 * array-like of its values; typed-array conversion applies (wrapping, rounding).
 * @param {import('./MsgPackWriter.js').MsgPackWriter} writer
 * @param {Readonly<ElementKind>} element
 * @param {ArrayLike<number> | ArrayLike<bigint> | null} value
 * @returns {void}
 */
export declare function writePrimitiveArray(writer: import('./MsgPackWriter.js').MsgPackWriter, element: Readonly<ElementKind>, value: ArrayLike<number> | ArrayLike<bigint> | null): void;
/**
 * Reads an ext 78 array of `element` into a new typed array, or null for nil.
 * @param {import('./MsgPackReader.js').MsgPackReader} reader
 * @param {Readonly<ElementKind>} element
 * @returns {any} The element's typed array, or null.
 */
export declare function readPrimitiveArray(reader: import('./MsgPackReader.js').MsgPackReader, element: Readonly<ElementKind>): any;
