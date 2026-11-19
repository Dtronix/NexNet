/**
 * The channel item prefix (protocol spec §7.4, C# `ChannelFraming`): the item's length in bytes as an unsigned
 * LEB128 varint (7 bits per byte, low bits first, high bit set on every byte but the last), at most 5 bytes.
 * @module
 */

/** Longest prefix: five bytes hold any length up to 2^31 - 1. */
export const MAX_PREFIX_LENGTH = 5;

/** Largest length a prefix may carry. */
const MAX_LENGTH = 0x7fffffff;

/** A malformed channel item prefix. */
export class ChannelFramingError extends Error {}

/**
 * Bytes needed to write `length` as a prefix.
 * @param {number} length
 * @returns {number}
 */
export function prefixLength(length) {
  let bytes = 1;
  for (let value = length; value >= 0x80; value = Math.floor(value / 128)) bytes++;
  return bytes;
}

/**
 * Writes `length` as a prefix into `destination` at `offset`. Returns the bytes written.
 * @param {Uint8Array} destination
 * @param {number} offset
 * @param {number} length
 * @returns {number}
 */
export function writeLength(destination, offset, length) {
  let value = length;
  let i = offset;
  while (value >= 0x80) {
    destination[i++] = (value & 0x7f) | 0x80;
    value = Math.floor(value / 128);
  }
  destination[i++] = value;
  return i - offset;
}

/**
 * Reads a prefix from the start of `bytes`. Returns null when `bytes` ends inside it.
 * @param {Uint8Array} bytes
 * @returns {{ length: number, size: number } | null}
 * @throws {ChannelFramingError} The prefix is longer than five bytes or exceeds 2^31 - 1.
 */
export function readLength(bytes) {
  let value = 0;
  for (let i = 0; i < MAX_PREFIX_LENGTH; i++) {
    if (i >= bytes.length) return null;
    const b = bytes[i];
    value += (b & 0x7f) * 2 ** (7 * i);
    if ((b & 0x80) === 0) {
      if (value > MAX_LENGTH) throw new ChannelFramingError('Channel item length prefix exceeds the maximum length.');
      return { length: value, size: i + 1 };
    }
  }
  throw new ChannelFramingError('Channel item length prefix is longer than five bytes.');
}
