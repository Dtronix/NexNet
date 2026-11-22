/**
 * Log levels and behaviors. Mirror C# `NexusLogLevel` and `NexusLogBehaviors`.
 * @module
 */
/**
 * Numeric order of the contract's level names (C# `NexusLogLevel` values). `none` is only a minimum level: it
 * turns a logger off.
 */
export declare const NexusLogLevelOrder: Readonly<{
    trace: 0;
    debug: 1;
    information: 2;
    warning: 3;
    error: 4;
    critical: 5;
    none: 6;
}>;
export type NexusMinLogLevel = import('../contract/config.js').NexusLogLevel | 'none';
/**
 * A level name, or `'none'` as a minimum level.
 * @typedef {import('../contract/config.js').NexusLogLevel | 'none'} NexusMinLogLevel
 */
/** Three-letter level tags written by the formatting loggers, as in C# `CoreLogger`. */
export declare const NexusLogLevelTag: Readonly<{
    trace: "TRA";
    debug: "DEB";
    information: "INF";
    warning: "WAR";
    error: "ERR";
    critical: "CRI";
    none: "NON";
}>;
/**
 * Logger behavior flags. Mirrors C# `NexusLogBehaviors`.
 */
export declare const NexusLogBehaviors: Readonly<{
    Default: 0;
    /** Log proxy (client-to-server) invocations as `information` instead of `debug`. */
    ProxyInvocationsLogAsInfo: number;
    /** Log local (server-to-client) invocations as `information` instead of `debug`. */
    LocalInvocationsLogAsInfo: number;
    /** Log the data sent and received. Debugging only. Authentication tokens are never logged. */
    LogTransportData: number;
}>;
/**
 * @param {unknown} level
 * @returns {number} The order of `level`; throws on an unknown name.
 */
export declare function levelOrder(level: unknown): number;
