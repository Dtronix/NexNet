/**
 * Wire constants of the NexNet protocol (docs/internals/protocol-specification.md §1–§3, §5, §9).
 * @module
 */

/** Message type bytes (protocol spec §3). Bytes 20–38 are disconnect reasons, see {@link isDisconnectCode}. */
export const MessageType = Object.freeze({
  Ping: 1,
  DuplexPipeWrite: 50,
  PipeWindowUpdate: 51,
  ClientGreeting: 100,
  ClientGreetingReconnection: 101,
  ServerGreeting: 105,
  Invocation: 110,
  InvocationCancellation: 111,
  InvocationResult: 112,
  MessageStart: 113,
  MessageChunk: 114,
  MessageAbort: 115,
  InvocationCredit: 116,
  DuplexPipeUpdateState: 120,
});

/** @typedef {typeof MessageType[keyof typeof MessageType]} MessageTypeValue */

/** The 8-byte connection preamble: magic `NnP\x14`, three reserved zero bytes, protocol version 1 (§1). */
export const PREAMBLE = Object.freeze([0x4e, 0x6e, 0x50, 0x14, 0, 0, 0, 1]);

/** Protocol version carried in the last preamble byte. */
export const PROTOCOL_VERSION = 1;

/** Largest frame body (§2). */
export const MAX_FRAME_BODY = 0xffff;

/** Largest frame body accepted before the connection completes (§2, §5.1). */
export const MAX_PRE_AUTH_BODY = 16 * 1024;

/** Smallest `maxMessageSize` a peer may advertise: one frame (65,526 bytes, §4.2). */
export const MIN_PEER_MAX_MESSAGE_SIZE = 0xffff - 9;

/** Largest greeting authentication token (§5.4). */
export const MAX_AUTHENTICATION_TOKEN = 8192;

/** Largest greeting version string, in UTF-16 code units (§5.2). */
export const MAX_VERSION_LENGTH = 16;

/** Inclusive range of disconnect codes (§3, §5.6). */
export const DISCONNECT_MIN = 20;
export const DISCONNECT_MAX = 38;

/**
 * True when `byte` is a disconnect code (20–38). Assigned or not, every code in the range ends the session (§5.6).
 * @param {number} byte
 * @returns {boolean}
 */
export function isDisconnectCode(byte) {
  return byte >= DISCONNECT_MIN && byte <= DISCONNECT_MAX;
}

/**
 * Frame layout of a message type: whether it has a 2-byte body length, and the post-header length (§2).
 * Null for a type that is not defined (a protocol error) and for disconnect codes.
 *
 * @param {number} type
 * @returns {{ hasBody: boolean, postHeader: number } | null}
 */
export function frameLayout(type) {
  switch (type) {
    case MessageType.Ping:
      return NO_BODY;
    case MessageType.DuplexPipeWrite:
    case MessageType.PipeWindowUpdate:
      return PIPE_HEADER;
    case MessageType.MessageChunk:
      return CHUNK_HEADER;
    case MessageType.ClientGreeting:
    case MessageType.ClientGreetingReconnection:
    case MessageType.ServerGreeting:
    case MessageType.Invocation:
    case MessageType.InvocationCancellation:
    case MessageType.InvocationResult:
    case MessageType.MessageStart:
    case MessageType.MessageAbort:
    case MessageType.InvocationCredit:
    case MessageType.DuplexPipeUpdateState:
      return BODY;
    default:
      return null;
  }
}

const NO_BODY = Object.freeze({ hasBody: false, postHeader: 0 });
const BODY = Object.freeze({ hasBody: true, postHeader: 0 });
const PIPE_HEADER = Object.freeze({ hasBody: true, postHeader: 2 });
const CHUNK_HEADER = Object.freeze({ hasBody: true, postHeader: 3 });
