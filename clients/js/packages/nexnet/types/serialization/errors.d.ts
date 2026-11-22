/**
 * The error a {@link import('./MsgPackReader.js').MsgPackReader} and the built-in formatters throw for input they refuse.
 * @module
 */
import { NexusError } from '../contract/errors.js';
export type SerializationErrorKind = 'truncated' | 'type' | 'reserved' | 'range' | 'count' | 'size' | 'length' | 'elements' | 'depth' | 'utf8' | 'value' | 'trailing';
/**
 * Why a value failed to decode. The kinds of the golden format (`tests/golden/README.md`, "Error kinds").
 *
 * | Kind | Meaning |
 * |---|---|
 * | `truncated` | the input ends inside the value |
 * | `type` | a MessagePack type the expected type cannot be read from (nil for a value type, a float for an integer…) |
 * | `reserved` | the never-used code `0xc1` |
 * | `range` | an integer outside the target type |
 * | `count` | an array with the wrong number of elements for a fixed shape |
 * | `size` | a wrong byte length for a fixed-size bin or for ext 78 data |
 * | `length` | a declared str/bin/ext length or array/map count larger than the remaining input |
 * | `elements` | more elements than `maxElements` |
 * | `depth` | more nesting than `maxDepth` |
 * | `utf8` | invalid UTF-8 with `strictUtf8` |
 * | `value` | a well-formed but invalid value (a date out of range, a decimal scale above 28, an unknown union tag…) |
 * | `trailing` | bytes after the value |
 *
 * @typedef {'truncated' | 'type' | 'reserved' | 'range' | 'count' | 'size' | 'length' | 'elements' | 'depth' | 'utf8' | 'value' | 'trailing'} SerializationErrorKind
 */
/**
 * Input that does not decode. Mirrors C# `NexusSerializationException`. Writers throw `TypeError`/`RangeError` for
 * values they cannot write, never this.
 */
export declare class NexusSerializationError extends NexusError {
    /**
     * Why the input was refused.
     * @readonly
     * @type {SerializationErrorKind}
     */
    kind: SerializationErrorKind;
    /**
     * @param {SerializationErrorKind} kind
     * @param {string} message
     * @param {{ cause?: unknown }} [options]
     */
    constructor(kind: SerializationErrorKind, message: string, options?: {
        cause?: unknown;
    });
}
/**
 * @param {SerializationErrorKind} kind
 * @param {string} message
 * @param {unknown} [cause]
 * @returns {NexusSerializationError}
 */
export declare function serializationError(kind: SerializationErrorKind, message: string, cause?: unknown): NexusSerializationError;
