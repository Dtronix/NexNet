/**
 * The channel item prefix (protocol spec §7.4, C# `ChannelFraming`): the item's length in bytes as an unsigned
 * LEB128 varint (7 bits per byte, low bits first, high bit set on every byte but the last), at most 5 bytes.
 * @module
 */
/** Longest prefix: five bytes hold any length up to 2^31 - 1. */
export declare const MAX_PREFIX_LENGTH = 5;
/** A malformed channel item prefix. */
export declare class ChannelFramingError extends Error {
}
/**
 * Bytes needed to write `length` as a prefix.
 * @param {number} length
 * @returns {number}
 */
export declare function prefixLength(length: number): number;
/**
 * Writes `length` as a prefix into `destination` at `offset`. Returns the bytes written.
 * @param {Uint8Array} destination
 * @param {number} offset
 * @param {number} length
 * @returns {number}
 */
export declare function writeLength(destination: Uint8Array, offset: number, length: number): number;
/**
 * Reads a prefix from the start of `bytes`. Returns null when `bytes` ends inside it.
 * @param {Uint8Array} bytes
 * @returns {{ length: number, size: number } | null}
 * @throws {ChannelFramingError} The prefix is longer than five bytes or exceeds 2^31 - 1.
 */
export declare function readLength(bytes: Uint8Array): {
    length: number;
    size: number;
} | null;
