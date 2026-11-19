/**
 * C# `System.Version`.
 * @module
 */
/**
 * C# `System.Version`. `build` and `revision` are -1 when absent, as in C#.
 * Exported as both `NexusVersion` and `Version` (the name the G03 type mapper emits).
 */
export declare class NexusVersion {
    /**
     * @readonly
     * @type {number}
     */
    readonly major: number;
    /**
     * @readonly
     * @type {number}
     */
    readonly minor: number;
    /**
     * -1 when absent.
     * @readonly
     * @type {number}
     */
    readonly build: number;
    /**
     * -1 when absent.
     * @readonly
     * @type {number}
     */
    readonly revision: number;
    /**
     * @param {number} major
     * @param {number} minor
     * @param {number} [build] -1 when absent.
     * @param {number} [revision] -1 when absent; needs `build`.
     */
    constructor(major: number, minor: number, build?: number, revision?: number);
    /**
     * Parses `major.minor[.build[.revision]]`: 2 to 4 non-negative int32 parts.
     * @param {string} text
     * @returns {NexusVersion}
     * @throws {SyntaxError} The text is not a version.
     */
    static parse(text: string): NexusVersion;
    /** As C# `Version.ToString()`: 2 to 4 parts. @returns {string} */
    toString(): string;
    /** @param {NexusVersion} other @returns {boolean} */
    equals(other: NexusVersion): boolean;
}
