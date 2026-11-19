/**
 * The MessagePack writer (protocol specification §10.1): canonical, smallest-form encoding into a growable buffer.
 * @module
 */
/**
 * Appends MessagePack to a growable buffer, always choosing the smallest (canonical) encoding (§10.1).
 * Multi-byte MessagePack values are big-endian. Methods throw `TypeError` for a value of the wrong JS type and
 * `RangeError` for one outside the MessagePack or target range; the buffer is unchanged then.
 */
export declare class MsgPackWriter {
    #private;
    /**
     * @param {{ initialCapacity?: number }} [options] `initialCapacity` defaults to 256 bytes.
     */
    constructor(options?: {
        initialCapacity?: number;
    });
    /** Number of bytes written so far. @returns {number} */
    get length(): number;
    /** Writes nil. @returns {void} */
    writeNil(): void;
    /** @param {boolean} value @returns {void} */
    writeBool(value: boolean): void;
    /**
     * Writes an integer that fits in a JS `number`: C# sbyte through uint, and `long`/`ulong` in their opt-in
     * checked-number mapping (D13). The target range is the formatter's job; this checks only that `value` is a safe
     * integer.
     * @param {number} value
     * @returns {void}
     */
    writeInt(value: number): void;
    /** Writes a signed 64-bit integer (C# `long`). @param {bigint} value @returns {void} */
    writeInt64(value: bigint): void;
    /** Writes an unsigned 64-bit integer (C# `ulong`). @param {bigint} value @returns {void} */
    writeUInt64(value: bigint): void;
    /** Writes float32 (C# `float`, and `Half` widened). The value is rounded to float32. @param {number} value @returns {void} */
    writeFloat32(value: number): void;
    /** Writes float64 (C# `double`). @param {number} value @returns {void} */
    writeFloat64(value: number): void;
    /**
     * Writes a UTF-8 string, or nil for null. A lone surrogate is written as U+FFFD, as C# and `TextEncoder` do.
     * @param {string | null} value
     * @returns {void}
     */
    writeString(value: string | null): void;
    /** Writes bin, or nil for null. @param {Uint8Array | null} value @returns {void} */
    writeBin(value: Uint8Array | null): void;
    /**
     * Writes the header of a bin of `length` bytes; the caller writes the bytes with {@link writeRaw}.
     * @param {number} length
     * @returns {void}
     */
    writeBinHeader(length: number): void;
    /** @param {number} count @returns {void} */
    writeArrayHeader(count: number): void;
    /** @param {number} count Number of key/value pairs. @returns {void} */
    writeMapHeader(count: number): void;
    /**
     * Writes an ext header; the caller writes the `length` data bytes with {@link writeRaw}.
     * @param {number} type Ext type, -128–127.
     * @param {number} length
     * @returns {void}
     */
    writeExtHeader(type: number, length: number): void;
    /** Writes an ext value. @param {number} type Ext type, -128–127. @param {Uint8Array} data @returns {void} */
    writeExt(type: number, data: Uint8Array): void;
    /** Appends bytes as they are (already-encoded MessagePack, or the data after a header). @param {Uint8Array} bytes @returns {void} */
    writeRaw(bytes: Uint8Array): void;
    /** Returns a view of the bytes written so far, valid until the next write or {@link reset}. @returns {Uint8Array} */
    toUint8Array(): Uint8Array;
    /** Clears the buffer for reuse; the capacity is kept. @returns {void} */
    reset(): void;
}
