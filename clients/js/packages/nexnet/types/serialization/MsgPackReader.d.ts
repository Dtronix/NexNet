/**
 * The MessagePack reader with the decoding limits of protocol specification §11 (`NexusSerializerOptions`).
 * @module
 */
export type SerializerOptions = {
    /**
     * Preset limits; explicit fields override it. Default `'untrusted'`.
     */
    preset?: 'trusted' | 'untrusted';
    /**
     * Most nesting in one value (64 untrusted, unlimited trusted). Each array or map of a
     * sequence, set, map, tuple, `KeyValuePair`, object or union is one level.
     */
    maxDepth?: number;
    /**
     * Most array elements plus map keys and values, counted over the whole value read
     * by one reader. 0 (the default) is no limit.
     */
    maxElements?: number;
    /**
     * Reject invalid UTF-8 instead of replacing it (true untrusted, false trusted).
     */
    strictUtf8?: boolean;
};
export type ResolvedSerializerOptions = {
    preset: 'trusted' | 'untrusted';
    maxDepth: number;
    maxElements: number;
    strictUtf8: boolean;
};
/**
 * Reader hardening, mirroring C# `NexusSerializerOptions`.
 *
 * @typedef {object} SerializerOptions
 * @property {'trusted' | 'untrusted'} [preset] Preset limits; explicit fields override it. Default `'untrusted'`.
 * @property {number} [maxDepth] Most nesting in one value (64 untrusted, unlimited trusted). Each array or map of a
 *   sequence, set, map, tuple, `KeyValuePair`, object or union is one level.
 * @property {number} [maxElements] Most array elements plus map keys and values, counted over the whole value read
 *   by one reader. 0 (the default) is no limit.
 * @property {boolean} [strictUtf8] Reject invalid UTF-8 instead of replacing it (true untrusted, false trusted).
 */
/**
 * Options with every member set.
 * @typedef {object} ResolvedSerializerOptions
 * @property {'trusted' | 'untrusted'} preset
 * @property {number} maxDepth
 * @property {number} maxElements
 * @property {boolean} strictUtf8
 */
/** The presets of C# `NexusSerializerOptions`. */
export declare const NexusSerializerOptions: Readonly<{
    /** Untrusted peer (the default): depth 64, strict UTF-8. @type {Readonly<ResolvedSerializerOptions>} */
    Untrusted: Readonly<ResolvedSerializerOptions>;
    /** Trusted peer: no depth limit, invalid UTF-8 replaced by U+FFFD. @type {Readonly<ResolvedSerializerOptions>} */
    Trusted: Readonly<ResolvedSerializerOptions>;
}>;
/**
 * Applies the preset and the defaults to `options`.
 * @param {SerializerOptions | null | undefined} options
 * @returns {Readonly<ResolvedSerializerOptions>}
 */
export declare function resolveSerializerOptions(options: SerializerOptions | null | undefined): Readonly<ResolvedSerializerOptions>;
/**
 * Reads MessagePack from one buffer or a list of segments, enforcing {@link SerializerOptions}.
 *
 * Every read checks the bytes it needs and throws {@link import('./errors.js').NexusSerializationError} for input it
 * refuses; its `kind` says why. Counts and lengths are checked against the remaining bytes before anything is
 * allocated. Skipping is iterative, so nesting costs no stack.
 */
export declare class MsgPackReader {
    #private;
    /**
     * @param {Uint8Array | readonly Uint8Array[]} source One buffer, or segments read in order. Not copied: do not change
     *   the bytes while reading.
     * @param {SerializerOptions} [options]
     */
    constructor(source: Uint8Array | readonly Uint8Array[], options?: SerializerOptions);
    /** The options in effect, with defaults applied. @returns {Readonly<ResolvedSerializerOptions>} */
    get options(): Readonly<ResolvedSerializerOptions>;
    /** Bytes not read yet. @returns {number} */
    get remaining(): number;
    /** Bytes read so far. @returns {number} */
    get consumed(): number;
    /** Current nesting depth (see {@link enter}). @returns {number} */
    get depth(): number;
    /**
     * Enters a nested container: call before reading the array or map of a sequence, set, map, tuple,
     * `KeyValuePair`, object or union, and {@link exit} after it. Throws (`depth`) past `maxDepth`.
     * @returns {void}
     */
    enter(): void;
    /** Leaves the container entered last. @returns {void} */
    exit(): void;
    /** Throws (`trailing`) unless every byte was read. @returns {void} */
    ensureEnd(): void;
    /** The next type code, not consumed. Throws (`truncated`) at the end. @returns {number} */
    peekCode(): number;
    /** True when the next value is nil (not consumed). @returns {boolean} */
    get isNil(): boolean;
    /** Consumes a nil and returns true, or returns false and consumes nothing. @returns {boolean} */
    tryReadNil(): boolean;
    /** Reads a nil. @returns {void} */
    readNil(): void;
    /** @returns {boolean} */
    readBool(): boolean;
    /**
     * Reads any integer form as a `number`. Throws (`range`) when the value is not a safe integer.
     * @returns {number}
     */
    readInt(): number;
    /** Reads a C# `sbyte`. @returns {number} */
    readSByte(): number;
    /** Reads a C# `byte`. @returns {number} */
    readByte(): number;
    /** Reads a C# `short`. @returns {number} */
    readInt16(): number;
    /** Reads a C# `ushort` (also `char` and union tags). @returns {number} */
    readUInt16(): number;
    /** Reads a C# `int`. @returns {number} */
    readInt32(): number;
    /** Reads a C# `uint`. @returns {number} */
    readUInt32(): number;
    /** Reads a C# `long`. @returns {bigint} */
    readInt64(): bigint;
    /** Reads a C# `ulong`. @returns {bigint} */
    readUInt64(): bigint;
    /**
     * Reads float32 or float64, or any integer form converted to the nearest double (§10.6). The caller rounds to
     * float32 or half when the declared type is narrower.
     * @returns {number}
     */
    readFloat(): number;
    /**
     * Reads a string, or null for nil. Invalid UTF-8 throws (`utf8`) with `strictUtf8`; otherwise each maximal
     * invalid subpart becomes U+FFFD.
     * @returns {string | null}
     */
    readString(): string | null;
    /**
     * Reads the length of a bin, or -1 for nil, leaving the reader at the bytes. Also accepts the str forms (older
     * writers sent bytes as raw strings).
     * @returns {number}
     */
    readBinHeader(): number;
    /** Reads bin (or str) as a new array, or null for nil. @returns {Uint8Array | null} */
    readBin(): Uint8Array | null;
    /**
     * Consumes `length` raw bytes (not a MessagePack value) as a new array.
     * @param {number} length
     * @returns {Uint8Array}
     */
    readBytes(length: number): Uint8Array;
    /**
     * Reads an array header and returns its element count, checked against the remaining bytes (`length`) and
     * `maxElements` (`elements`). With `expected`, a different count throws (`count`).
     * @param {number} [expected]
     * @returns {number}
     */
    readArrayHeader(expected?: number): number;
    /**
     * Reads a map header and returns its entry count, checked against the remaining bytes (`length`) and
     * `maxElements` (each entry counts its key and its value).
     * @returns {number}
     */
    readMapHeader(): number;
    /**
     * Reads an ext header and returns its type (-128–127) and data length, checked against the remaining bytes.
     * @returns {{ type: number, length: number }}
     */
    readExtHeader(): {
        type: number;
        length: number;
    };
    /** Reads an ext value; `data` is a new array. @returns {{ type: number, data: Uint8Array }} */
    readExt(): {
        type: number;
        data: Uint8Array;
    };
    /**
     * Skips one complete value of any type. Iterative: nesting costs no stack. Counts neither depth nor elements.
     * @returns {void}
     */
    skip(): void;
    /**
     * Consumes one complete value and returns its bytes (a new array): an embedded value carried as is, such as the
     * arguments of an invocation.
     * @returns {Uint8Array}
     */
    readRaw(): Uint8Array;
}
