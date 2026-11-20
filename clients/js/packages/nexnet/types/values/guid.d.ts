/**
 * C# `Guid`. JS represents a Guid as its lower-case "D" string, `00112233-4455-6677-8899-aabbccddeeff`.
 * @module
 */
/** The empty Guid. */
export declare const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
/**
 * Normalizes a Guid string: the "D" form (any case), the "N" form (32 hex digits) or the "B" form (`{D}`).
 * @param {string} text
 * @returns {string} The lower-case "D" form.
 * @throws {SyntaxError} Not a Guid.
 */
export declare function parseGuid(text: string): string;
/**
 * The 16 bytes of a Guid in the order of its text (RFC 4122, big-endian): the wire form (§10.6), C#
 * `Guid.ToByteArray(bigEndian: true)`.
 * @param {string} guid
 * @returns {Uint8Array}
 */
export declare function guidToBytes(guid: string): Uint8Array;
/**
 * The Guid of 16 bytes in RFC 4122 (big-endian) order.
 * @param {Uint8Array} bytes
 * @returns {string} The lower-case "D" form.
 */
export declare function guidFromBytes(bytes: Uint8Array): string;
/**
 * The bytes of C# `Guid.ToByteArray()` (mixed-endian, the .NET default). Not the wire form.
 * @param {string} guid
 * @returns {Uint8Array}
 */
export declare function guidToDotNetBytes(guid: string): Uint8Array;
/**
 * The Guid of the bytes of C# `Guid.ToByteArray()` (`new Guid(byte[])`).
 * @param {Uint8Array} bytes
 * @returns {string}
 */
export declare function guidFromDotNetBytes(bytes: Uint8Array): string;
