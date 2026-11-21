/**
 * Where the invocation layer gets its payload codec: argument writers, result readers and dispatch readers. The
 * default is the public `MsgPackWriter`/`MsgPackReader`. Tests may swap in another codec with
 * {@link setInvocationCodec}; generated code never does.
 * @module
 */

import { MsgPackReader, MsgPackWriter } from '../contract/codec.js';

/**
 * @typedef {object} InvocationCodec
 * @property {() => MsgPackWriter} createWriter A fresh writer for one argument list or result.
 * @property {(bytes: Uint8Array, options?: import('../contract/codec.js').SerializerOptions) => MsgPackReader} createReader
 *   A reader over one argument list or result value.
 */

/** @type {InvocationCodec} */
const defaultCodec = Object.freeze({
  createWriter: () => new MsgPackWriter(),
  createReader: (bytes, options) => new MsgPackReader(bytes, options),
});

let current = defaultCodec;

/** The codec in use. @returns {InvocationCodec} */
export function invocationCodec() {
  return current;
}

/**
 * Replaces the codec; null restores the default. Returns the previous one.
 * @param {InvocationCodec | null} codec
 * @returns {InvocationCodec}
 */
export function setInvocationCodec(codec) {
  const previous = current;
  current = codec ?? defaultCodec;
  return previous;
}

/**
 * Serializes an argument list (or a result) with `write`, returning a copy of the bytes.
 * @param {(writer: MsgPackWriter) => void} write
 * @returns {Uint8Array}
 */
export function serialize(write) {
  const writer = current.createWriter();
  write(writer);
  return writer.toUint8Array().slice();
}
