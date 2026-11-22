/**
 * Logger that keeps the last lines in memory. Mirrors C# `RollingLogger`: tests dump it when they fail.
 * @module
 */
import { CoreLogger } from './CoreLogger.js';
export type RollingBuffer = {
    lines: string[];
    next: number;
    total: number;
};
/** @typedef {{ lines: string[], next: number, total: number }} RollingBuffer */
/** Keeps the last `maxLines` lines of the whole logger tree in one ring buffer. */
export declare class RollingLogger extends CoreLogger {
    #private;
    /**
     * @param {object} [options]
     * @param {number} [options.maxLines] Default 200.
     * @param {import('./levels.js').NexusMinLogLevel} [options.minLevel] Default `'trace'`.
     * @param {string | null} [options.pathSegment]
     * @param {() => number} [options.now]
     * @param {RollingLogger} [options.parent] Internal: use {@link RollingLogger#child}.
     */
    constructor(options?: {
        maxLines?: number;
        minLevel?: import('./levels.js').NexusMinLogLevel;
        pathSegment?: string | null;
        now?: () => number;
        parent?: RollingLogger;
    });
    /** Lines written since the last flush, including those rolled out of the buffer. @returns {number} */
    get totalLinesWritten(): number;
    /**
     * @param {import('../contract/config.js').NexusLogLevel} level
     * @param {string} line
     */
    write(level: import('../contract/config.js').NexusLogLevel, line: string): void;
    /** The buffered lines, oldest first. @returns {string[]} */
    lines(): string[];
    /**
     * Writes the buffered lines, oldest first, preceded by a truncation notice when lines were rolled out, then clears
     * the buffer (C# `Flush(TextWriter)`).
     * @param {(line: string) => void} writeLine
     * @returns {void}
     */
    flush(writeLine: (line: string) => void): void;
    /** @param {string | null} pathSegment @returns {RollingLogger} */
    createChild(pathSegment: string | null): RollingLogger;
    /** @param {string} path @returns {RollingLogger} */
    child(path: string): RollingLogger;
}
