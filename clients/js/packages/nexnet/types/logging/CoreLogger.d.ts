/**
 * Base of the formatting loggers: hierarchical paths, shared minimum level and behaviors, and the C# `CoreLogger`
 * line format `[seconds][LVL] [path(category)] message error`.
 * @module
 */
export type NexusLogLevel = import('../contract/config.js').NexusLogLevel;
export type NexusMinLogLevel = import('./levels.js').NexusMinLogLevel;
export type LoggerRoot = {
    /**
     * Clock time the root was created.
     */
    start: number;
    now: () => number;
    minLevel: number;
    behaviors: number;
    enabled: boolean;
};
export type PathNode = {
    parent: PathNode | null;
    segment: string | null;
};
/** @typedef {import('../contract/config.js').NexusLogLevel} NexusLogLevel */
/** @typedef {import('./levels.js').NexusMinLogLevel} NexusMinLogLevel */
/**
 * State shared by a root logger and every logger created from it.
 * @typedef {object} LoggerRoot
 * @property {number} start Clock time the root was created.
 * @property {() => number} now
 * @property {number} minLevel
 * @property {number} behaviors
 * @property {boolean} enabled
 */
/**
 * One node of the logger path. A child keeps a reference to its parent node, so renaming a parent's segment renames
 * every descendant's path (C# `PathNode`).
 * @typedef {{ parent: PathNode | null, segment: string | null }} PathNode
 */
/**
 * Renders the `error` argument of a log call. Byte arrays are never written out (tokens are bytes): only their length.
 * @param {unknown} error
 * @returns {string}
 */
export declare function describeError(error: unknown): string;
/**
 * A {@link import('../contract/config.js').NexusLogger} that formats lines. Subclasses implement {@link CoreLogger#write}
 * and {@link CoreLogger#createChild}.
 *
 * @abstract
 */
export declare class CoreLogger {
    #private;
    /**
     * @param {object} [options]
     * @param {CoreLogger} [options.parent] The logger this one is created from; shares its level, behaviors and clock.
     * @param {string | null} [options.pathSegment]
     * @param {NexusMinLogLevel} [options.minLevel] Root only. Default `'trace'`.
     * @param {() => number} [options.now] Root only. Milliseconds; default `performance.now()`.
     */
    constructor({ parent, pathSegment, minLevel, now }?: {
        parent?: CoreLogger;
        pathSegment?: string | null;
        minLevel?: NexusMinLogLevel;
        now?: () => number;
    });
    /** Behavior flags ({@link NexusLogBehaviors}), shared by the whole logger tree. @returns {number} */
    get behaviors(): number;
    set behaviors(value: number);
    /** Lowest level written, shared by the whole logger tree. @returns {NexusMinLogLevel} */
    get minLevel(): NexusMinLogLevel;
    set minLevel(level: NexusMinLogLevel);
    /** False turns the whole logger tree off. @returns {boolean} */
    get enabled(): boolean;
    set enabled(value: boolean);
    /** This logger's own path segment; may be renamed. @returns {string | null} */
    get pathSegment(): string | null;
    set pathSegment(value: string | null);
    /** The non-blank segments from the root to this logger, joined with `|`. @returns {string} */
    get formattedPath(): string;
    /**
     * True when a message of `level` would be written.
     * @param {NexusLogLevel} level
     * @returns {boolean}
     */
    isEnabled(level: NexusLogLevel): boolean;
    /**
     * @param {NexusLogLevel} level
     * @param {string} message
     * @param {unknown} [error]
     * @returns {void}
     */
    log(level: NexusLogLevel, message: string, error?: unknown): void;
    /**
     * Creates a logger for a sub-path (`'Session 3'`, `'Pipe 7'`).
     * @param {string} path
     * @returns {CoreLogger}
     */
    child(path: string): CoreLogger;
    /**
     * Formats a line as C# `CoreLogger` does, or returns null when the level is filtered out.
     * @param {NexusLogLevel} level
     * @param {string} message
     * @param {unknown} [error]
     * @param {string | null} [category]
     * @returns {string | null}
     */
    format(level: NexusLogLevel, message: string, error?: unknown, category?: string | null): string | null;
    /**
     * Writes a formatted line.
     * @abstract
     * @param {NexusLogLevel} level
     * @param {string} line
     * @returns {void}
     */
    write(level: NexusLogLevel, line: string): void;
    /**
     * @abstract
     * @param {string | null} pathSegment
     * @returns {CoreLogger}
     */
    createChild(pathSegment: string | null): CoreLogger;
}
