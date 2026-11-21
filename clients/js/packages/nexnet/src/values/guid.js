/**
 * C# `Guid`. JS represents a Guid as its lower-case "D" string, `00112233-4455-6677-8899-aabbccddeeff`.
 * @module
 */

const PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** The empty Guid. */
export const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/**
 * Normalizes a Guid string: the "D" form (any case), the "N" form (32 hex digits) or the "B" form (`{D}`).
 * @param {string} text
 * @returns {string} The lower-case "D" form.
 * @throws {SyntaxError} Not a Guid.
 */
export function parseGuid(text) {
  if (typeof text !== 'string') throw new TypeError('a Guid is a string');
  let s = text.toLowerCase();
  if (s.length === 38 && s[0] === '{' && s[37] === '}') s = s.slice(1, 37);
  if (s.length === 32 && /^[0-9a-f]{32}$/.test(s)) s = `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
  if (!PATTERN.test(s)) throw new SyntaxError(`not a Guid: ${text}`);
  return s;
}

/**
 * The 16 bytes of a Guid in the order of its text (RFC 4122, big-endian): the wire form (§10.6), C#
 * `Guid.ToByteArray(bigEndian: true)`.
 * @param {string} guid
 * @returns {Uint8Array}
 */
export function guidToBytes(guid) {
  const hex = parseGuid(guid).replaceAll('-', '');
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  return bytes;
}

/**
 * The Guid of 16 bytes in RFC 4122 (big-endian) order.
 * @param {Uint8Array} bytes
 * @returns {string} The lower-case "D" form.
 */
export function guidFromBytes(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length !== 16) throw new RangeError('a Guid is 16 bytes');
  let hex = '';
  for (let i = 0; i < 16; i++) {
    hex += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16);
    if (i === 3 || i === 5 || i === 7 || i === 9) hex += '-';
  }
  return hex;
}

/**
 * Swaps between RFC 4122 order and the mixed-endian order of C# `Guid.ToByteArray()` (the first three groups
 * little-endian). The swap is its own inverse.
 * @param {Uint8Array} bytes 16 bytes.
 * @returns {Uint8Array} A new array.
 */
function swapGroups(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length !== 16) throw new RangeError('a Guid is 16 bytes');
  const b = bytes.slice();
  b.subarray(0, 4).reverse();
  b.subarray(4, 6).reverse();
  b.subarray(6, 8).reverse();
  return b;
}

/**
 * The bytes of C# `Guid.ToByteArray()` (mixed-endian, the .NET default). Not the wire form.
 * @param {string} guid
 * @returns {Uint8Array}
 */
export function guidToDotNetBytes(guid) {
  return swapGroups(guidToBytes(guid));
}

/**
 * The Guid of the bytes of C# `Guid.ToByteArray()` (`new Guid(byte[])`).
 * @param {Uint8Array} bytes
 * @returns {string}
 */
export function guidFromDotNetBytes(bytes) {
  return guidFromBytes(swapGroups(bytes));
}
