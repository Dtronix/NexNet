/**
 * Client side of a server `INexusList<T>` (protocol spec §12, C# `NexusListClient` and `NexusBroadcastClient`).
 * @module
 */
export type NexusListChange<T> = import('./ListReplica.js').NexusListChange<T>;
export type NexusListEventMap<T> = {
    /**
     * Follows each change.
     */
    changed: CustomEvent<NexusListChange<T>>;
};
export type NexusListOptions<T> = {
    /**
     * The `[NexusCollection]` mode of the server
     * nexus property. Default `ServerToClient`, as in C#.
     */
    mode?: import('./mode.js').NexusCollectionModeValue;
    /**
     * Item equality for `remove(item)`, `indexOf` and `contains`
     * (protocol spec §12.9). Default {@link defaultEquals}.
     */
    equals?: (a: T, b: T) => boolean;
    /**
     * The formatter of C# `T[]`, which
     * reads the values of a reset. Needed when C# writes `T[]` as ext 78 (arrays of primitive numeric types, protocol
     * spec §10.5). Default: a MessagePack array of items.
     */
    valuesFormatter?: import('../contract/codec.js').Formatter<T[] | null>;
};
/**
 * @template T
 * @typedef {import('./ListReplica.js').NexusListChange<T>} NexusListChange
 */
/**
 * The events of a {@link NexusList}, for `addEventListener`.
 *
 * @template T
 * @typedef {object} NexusListEventMap
 * @property {CustomEvent<NexusListChange<T>>} changed Follows each change.
 */
/**
 * How the runtime builds a list. Supplied by `ProxyBase._collection` from the generated proxy.
 *
 * @template T
 * @typedef {object} NexusListOptions
 * @property {import('./mode.js').NexusCollectionModeValue} [mode] The `[NexusCollection]` mode of the server
 *   nexus property. Default `ServerToClient`, as in C#.
 * @property {(a: T, b: T) => boolean} [equals] Item equality for `remove(item)`, `indexOf` and `contains`
 *   (protocol spec §12.9). Default {@link defaultEquals}.
 * @property {import('../contract/codec.js').Formatter<T[] | null>} [valuesFormatter] The formatter of C# `T[]`, which
 *   reads the values of a reset. Needed when C# writes `T[]` as ext 78 (arrays of primitive numeric types, protocol
 *   spec §10.5). Default: a MessagePack array of items.
 */
/**
 * The default item equality, C# `EqualityComparer<T>.Default` for the JS mappings: an item with an `equals` method
 * (the runtime value types, generated structs and records) uses it; anything else compares with SameValueZero, which
 * is `===` except that NaN equals NaN, as `double.Equals` does. Objects without `equals` compare by identity.
 *
 * @param {unknown} a
 * @param {unknown} b
 * @returns {boolean}
 */
export declare function defaultEquals(a: unknown, b: unknown): boolean;
/**
 * Client side of a server `INexusList<T>`. Get it from the generated proxy's collection property; the same instance
 * serves every session of the client.
 *
 * - **Enable** opens a collection pipe and resolves with true once the server's initial state arrived (`ready`); it
 *   resolves with false when the pipe closed first and rejects when the server refused it (`NexusBusyError`) or the
 *   session is not connected. Collections are not resumed after a reconnect: enable again.
 * - **Reads** (`count`, `get`, `at`, `indexOf`, `contains`, `toArray`, iteration) are local.
 * - **Mutations** (BiDirectional mode only) send one request at a time and resolve with true once the server applied
 *   it, false when the server discarded it (a concurrent change made it obsolete) or the collection closed. The list
 *   changes only when the server's change arrives; there is no optimistic local apply. A mutation made while
 *   `enable` runs waits for the initial state and is then sent with its version; it resolves with false when the enable
 *   fails. They reject with a `NexusError` in ServerToClient and Relay mode and when the list is not enabled, and with
 *   a `RangeError` for a negative index.
 * - A `changed` event (`CustomEvent` with a {@link NexusListChange} `detail`) follows each change.
 *
 * @template T
 */
export declare class NexusList<T> extends EventTarget {
    #private;
    /**
     * Created by the runtime (`ProxyBase._collection`).
     * @param {import('../client/ProxyBase.js').SessionHandle} handle
     * @param {number} methodId The collection ID.
     * @param {import('../contract/codec.js').Formatter<T>} formatter Item formatter.
     * @param {NexusListOptions<T>} [options]
     */
    constructor(handle: import('../client/ProxyBase.js').SessionHandle, methodId: number, formatter: import('../contract/codec.js').Formatter<T>, options?: NexusListOptions<T>);
    /** The collection mode (`NexusCollectionMode`). @returns {import('./mode.js').NexusCollectionModeValue} */
    get mode(): import('./mode.js').NexusCollectionModeValue;
    /** Connected once the initial state arrived; Disconnected before and after. @returns {import('../contract/enums.js').NexusCollectionStateValue} */
    get state(): import('../contract/enums.js').NexusCollectionStateValue;
    /** True from `enable` until the collection pipe closed. @returns {boolean} */
    get isEnabled(): boolean;
    /**
     * Resolves after the initial state arrived. Each `enable` starts a fresh promise; it rejects with a `NexusError`
     * when that enable fails or the collection closes before its first synchronization, and keeps its outcome after a
     * later close. Resolved before the first enable. Mirrors C# `ReadyTask`.
     * @returns {Promise<void>}
     */
    get ready(): Promise<void>;
    /** Resolves when the list is disabled or its session ends; resolved while not enabled. @returns {Promise<void>} */
    get disabled(): Promise<void>;
    /** @returns {number} */
    get count(): number;
    /** Same as {@link NexusList#count}. @returns {number} */
    get length(): number;
    /** The list version the replica is at (protocol spec §12.3). @returns {number} */
    get version(): number;
    /**
     * Test hook, not for applications (C# `NexusBroadcastClient.ProcessTestHook`): `hook` is called in the read loop
     * before each received list message is processed. While the promise it returns is pending, the list neither
     * processes nor reads anything more from its pipe, so it grants the server no pipe credit: a test makes a slow or
     * stalled consumer with it. A hook that throws or rejects is logged and ignored. `null` removes it; a message
     * already waiting for the removed hook still waits for its promise.
     * @param {(() => void | PromiseLike<void>) | null} hook
     * @returns {void}
     */
    setProcessTestHook(hook: (() => void | PromiseLike<void>) | null): void;
    /**
     * The item at `index`. Throws `RangeError` when out of range (C# indexer).
     * @param {number} index
     * @returns {T}
     */
    get(index: number): T;
    /**
     * The item at `index`, counting back from the end when negative; undefined when out of range (`Array.prototype.at`).
     * @param {number} index
     * @returns {T | undefined}
     */
    at(index: number): T | undefined;
    /** @param {T} item @returns {number} */
    indexOf(item: T): number;
    /** @param {T} item @returns {boolean} */
    contains(item: T): boolean;
    /** A copy of the items. @returns {T[]} */
    toArray(): T[];
    /** Iterates a snapshot of the items. @returns {Iterator<T>} */
    [Symbol.iterator](): Iterator<T>;
    /**
     * Subscribes to the server list (protocol spec §12.1). Resolves with true once the initial state arrived, at once
     * when already enabled (with the in-flight enable's outcome while it runs), and with false when the pipe closed
     * before the initial state. Rejects with `NexusTransportError` when not connected, `NexusBusyError` when a pipe
     * limit refused the pipe or the collection is not authorized, and the abort reason when `signal` aborts before the
     * pipe opened.
     *
     * @param {{ signal?: AbortSignal }} [options]
     * @returns {Promise<boolean>}
     */
    enable(options?: {
        signal?: AbortSignal;
    }): Promise<boolean>;
    /**
     * Closes the collection pipe and waits until it closed. The list empties and raises `reset`. Resolves at once when
     * not enabled.
     * @returns {Promise<void>}
     */
    disable(): Promise<void>;
    /** Inserts `item` at the end. @param {T} item @returns {Promise<boolean>} */
    add(item: T): Promise<boolean>;
    /** @param {number} index @param {T} item @returns {Promise<boolean>} */
    insert(index: number, item: T): Promise<boolean>;
    /**
     * Removes the first item equal to `item` (protocol spec §12.9). Resolves with false, sending nothing, when no item is
     * equal.
     * @param {T} item
     * @returns {Promise<boolean>}
     */
    remove(item: T): Promise<boolean>;
    /** @param {number} index @returns {Promise<boolean>} */
    removeAt(index: number): Promise<boolean>;
    /** @param {number} index @param {T} value @returns {Promise<boolean>} */
    replace(index: number, value: T): Promise<boolean>;
    /** @param {number} fromIndex @param {number} toIndex @returns {Promise<boolean>} */
    move(fromIndex: number, toIndex: number): Promise<boolean>;
    /** @returns {Promise<boolean>} */
    clear(): Promise<boolean>;
    /**
     * Typed for the events of {@link NexusListEventMap}; any other type takes a plain listener.
     * @template {keyof NexusListEventMap<T>} K
     * @overload
     * @param {K} type
     * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener<K extends keyof NexusListEventMap<T>>(type: K, listener: (this: NexusList<T>, event: NexusListEventMap<T>[K]) => void, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed for the events of {@link NexusListEventMap}; any other type takes a plain listener.
     * @template {keyof NexusListEventMap<T>} K
     * @overload
     * @param {K} type
     * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | AddEventListenerOptions} [options]
     */
    addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions): void;
    /**
     * Typed like {@link NexusList#addEventListener}.
     * @template {keyof NexusListEventMap<T>} K
     * @overload
     * @param {K} type
     * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener<K extends keyof NexusListEventMap<T>>(type: K, listener: (this: NexusList<T>, event: NexusListEventMap<T>[K]) => void, options?: boolean | EventListenerOptions): void;
    /**
     * Typed like {@link NexusList#addEventListener}.
     * @template {keyof NexusListEventMap<T>} K
     * @overload
     * @param {K} type
     * @param {(this: NexusList<T>, event: NexusListEventMap<T>[K]) => void} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @overload
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     * @returns {void}
     */
    /**
     * @param {string} type
     * @param {EventListenerOrEventListenerObject | null} listener
     * @param {boolean | EventListenerOptions} [options]
     */
    removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions): void;
}
