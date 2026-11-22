/**
 * Frame encoding (protocol spec §2). Header fields after the type byte are little-endian.
 * @module
 */
/** @returns {Uint8Array} A fresh copy of the 8-byte preamble. */
export declare function encodePreamble(): Uint8Array;
/**
 * A frame with no length and no body: `Ping` or a disconnect code.
 * @param {number} type
 * @returns {Uint8Array}
 */
export declare function encodeHeaderOnly(type: number): Uint8Array;
/**
 * A frame with a body and no post-header: `[type][length u16 LE][body]`.
 * @param {number} type
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export declare function encodeFrame(type: number, body: Uint8Array): Uint8Array;
/**
 * A pipe frame (`DuplexPipeWrite` or `PipeWindowUpdate`): `[type][length u16 LE][clientId][serverId][body]`.
 * `pipeId` is the MessagePack form, `clientId | serverId << 8` (§2 Pipe IDs).
 * @param {number} type
 * @param {number} pipeId
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export declare function encodePipeFrame(type: number, pipeId: number, body: Uint8Array): Uint8Array;
/**
 * A `PipeWindowUpdate` frame granting `credit` bytes (§7.2): the body is a `uint32` little-endian.
 * @param {number} pipeId
 * @param {number} credit
 * @returns {Uint8Array}
 */
export declare function encodePipeWindowUpdate(pipeId: number, credit: number): Uint8Array;
/**
 * A `MessageChunk` frame: `[114][length u16 LE][invocationId u16 LE][kind u8][body]` (§4.10).
 * @param {number} invocationId
 * @param {number} kind
 * @param {Uint8Array} body
 * @returns {Uint8Array}
 */
export declare function encodeChunkFrame(invocationId: number, kind: number, body: Uint8Array): Uint8Array;
