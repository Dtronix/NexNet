/**
 * Where the invocation layer gets its payload codec: argument writers, result readers and dispatch readers. The
 * default is the public `MsgPackWriter`/`MsgPackReader` (J02). Tests may swap in another codec with
 * {@link setInvocationCodec}; generated code never does.
 * @module
 */
import { MsgPackReader, MsgPackWriter } from '../contract/codec.js';
export type InvocationCodec = {
    /**
     * A fresh writer for one argument list or result.
     */
    createWriter: () => MsgPackWriter;
    /**
     *   A reader over one argument list or result value.
     */
    createReader: (bytes: Uint8Array, options?: import('../contract/codec.js').SerializerOptions) => MsgPackReader;
};
/** The codec in use. @returns {InvocationCodec} */
export declare function invocationCodec(): InvocationCodec;
/**
 * Replaces the codec; null restores the default. Returns the previous one.
 * @param {InvocationCodec | null} codec
 * @returns {InvocationCodec}
 */
export declare function setInvocationCodec(codec: InvocationCodec | null): InvocationCodec;
/**
 * Serializes an argument list (or a result) with `write`, returning a copy of the bytes.
 * @param {(writer: MsgPackWriter) => void} write
 * @returns {Uint8Array}
 */
export declare function serialize(write: (writer: MsgPackWriter) => void): Uint8Array;
