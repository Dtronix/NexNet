/**
 * The collection-sync channel items (protocol spec §4.11): a union value `[tag: uint16, body: array]` whose body starts
 * with `flags: uint8`. Mirrors C# `NexusCollectionListMessageFormatter` and the `NexusCollectionList*Message` classes.
 * @module
 */
/** Union tags of the list items (§4.11). */
export declare const ListMessageTag: Readonly<{
    ResetStart: 0;
    ResetValues: 1;
    ResetComplete: 2;
    Clear: 3;
    Insert: 4;
    Replace: 5;
    Move: 6;
    Remove: 7;
    Noop: 8;
}>;
export type ListMessageTagValue = typeof ListMessageTag[keyof typeof ListMessageTag];
/** @typedef {typeof ListMessageTag[keyof typeof ListMessageTag]} ListMessageTagValue */
/** Item flags (§12.3). Bit 0 is Ack; the other bits are reserved. */
export declare const CollectionMessageFlags: Readonly<{
    None: 0;
    Ack: 1;
}>;
export type ListMessage<T> = {
    tag: ListMessageTagValue;
    flags: number;
    /**
     * ResetStart: the snapshot version. Server change: the version after the change.
     * Client request: the base version.
     */
    version?: number;
    /**
     * ResetStart.
     */
    totalValues?: number;
    /**
     * ResetValues (null when the server wrote nil).
     */
    values?: T[] | null;
    /**
     * Insert, Replace, Remove.
     */
    index?: number;
    /**
     * Insert, Replace.
     */
    value?: T;
    /**
     * Move.
     */
    fromIndex?: number;
    /**
     * Move.
     */
    toIndex?: number;
};
/**
 * The formatter of a C# `T[]` written as a MessagePack array of items, or nil for null. C# writes arrays of most
 * primitive numeric types as ext 78 instead (protocol spec §10.5); generated code passes its own `T[]` formatter for
 * those.
 *
 * @template T
 * @param {import('../contract/codec.js').Formatter<T>} itemFormatter
 * @returns {import('../contract/codec.js').Formatter<T[] | null>}
 */
export declare function arrayFormatter<T>(itemFormatter: import('../contract/codec.js').Formatter<T>): import('../contract/codec.js').Formatter<T[] | null>;
/**
 * The channel formatter of the list items for one item type.
 *
 * @template T
 * @param {import('../contract/codec.js').Formatter<T>} itemFormatter
 * @param {import('../contract/codec.js').Formatter<T[] | null>} [valuesFormatter] The C# `T[]` formatter, for the
 *   values of ResetValues. Default {@link arrayFormatter}.
 * @returns {import('../contract/codec.js').Formatter<ListMessage<T>>}
 */
export declare function listMessageFormatter<T>(itemFormatter: import('../contract/codec.js').Formatter<T>, valuesFormatter?: import('../contract/codec.js').Formatter<T[] | null>): import('../contract/codec.js').Formatter<ListMessage<T>>;
