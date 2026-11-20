/**
 * Logger that writes to the console. Mirrors C# `ConsoleLogger`.
 * @module
 */
import { CoreLogger } from './CoreLogger.js';
export type ConsoleLike = {
    debug: (line: string) => void;
    info: (line: string) => void;
    warn: (line: string) => void;
    error: (line: string) => void;
};
/**
 * @typedef {object} ConsoleLike
 * @property {(line: string) => void} debug
 * @property {(line: string) => void} info
 * @property {(line: string) => void} warn
 * @property {(line: string) => void} error
 */
/** Writes each line to `console.debug`, `info`, `warn` or `error` by level. */
export declare class ConsoleLogger extends CoreLogger {
    #private;
    /**
     * @param {object} [options]
     * @param {import('./levels.js').NexusMinLogLevel} [options.minLevel] Default `'trace'`.
     * @param {string | null} [options.pathSegment]
     * @param {ConsoleLike} [options.console] Default: the global `console`.
     * @param {() => number} [options.now]
     * @param {ConsoleLogger} [options.parent] Internal: use {@link ConsoleLogger#child}.
     */
    constructor(options?: {
        minLevel?: import('./levels.js').NexusMinLogLevel;
        pathSegment?: string | null;
        console?: ConsoleLike;
        now?: () => number;
        parent?: ConsoleLogger;
    });
    /**
     * @param {import('../contract/config.js').NexusLogLevel} level
     * @param {string} line
     */
    write(level: import('../contract/config.js').NexusLogLevel, line: string): void;
    /** @param {string | null} pathSegment @returns {ConsoleLogger} */
    createChild(pathSegment: string | null): ConsoleLogger;
    /** @param {string} path @returns {ConsoleLogger} */
    child(path: string): ConsoleLogger;
}
