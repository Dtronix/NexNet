/**
 * C# `decimal` (protocol specification §10.6).
 * @module
 */
/**
 * C# `decimal`: a 96-bit magnitude, a scale of 0–28 and a sign. Exact; never a float. The scale is kept, so 1.5 and
 * 1.50 are different values on the wire but {@link equals} each other. A negative zero is valid.
 */
export declare class NexusDecimal {
    /**
     * Unsigned 96-bit magnitude.
     * @readonly
     * @type {bigint}
     */
    readonly magnitude: bigint;
    /**
     * Power of ten to divide by, 0–28.
     * @readonly
     * @type {number}
     */
    readonly scale: number;
    /**
     * @readonly
     * @type {boolean}
     */
    readonly negative: boolean;
    /**
     * @param {bigint} magnitude Unsigned 96-bit integer.
     * @param {number} scale Power of ten to divide by, 0–28.
     * @param {boolean} negative
     */
    constructor(magnitude: bigint, scale: number, negative: boolean);
    /**
     * Parses invariant-culture text: an optional sign, digits with an optional `.` fraction, and an optional exponent
     * (`1.5`, `-0.25`, `1e3`). Digits beyond a scale of 28 are rounded half to even, as C# `decimal.Parse` does.
     * @param {string} text
     * @returns {NexusDecimal}
     * @throws {SyntaxError} The text is not a number.
     * @throws {RangeError} The value is beyond the decimal range.
     */
    static parse(text: string): NexusDecimal;
    /**
     * The decimal nearest to `value`, from its shortest round-trip text.
     * @param {number} value A finite number.
     * @returns {NexusDecimal}
     */
    static fromNumber(value: number): NexusDecimal;
    /**
     * The value of C# `decimal.GetBits`: four signed int32, lo, mid, hi, flags. Throws (`RangeError`) for a scale
     * above 28 or a reserved flag bit.
     * @param {number} lo
     * @param {number} mid
     * @param {number} hi
     * @param {number} flags
     * @returns {NexusDecimal}
     */
    static fromBits(lo: number, mid: number, hi: number, flags: number): NexusDecimal;
    /** C# `decimal.GetBits`: `[lo, mid, hi, flags]`, each a signed int32. @returns {[number, number, number, number]} */
    toBits(): [number, number, number, number];
    /** Converts to the nearest double (lossy). A negative zero converts to -0. @returns {number} */
    toNumber(): number;
    /**
     * Invariant-culture text, as C# `decimal.ToString(CultureInfo.InvariantCulture)`: the scale's digits are kept
     * (`1.50`), and a negative zero has no sign.
     * @returns {string}
     */
    toString(): string;
    /**
     * Numeric equality: `1.0` equals `1.00`, and zero equals negative zero (C# `decimal.Equals`).
     * @param {NexusDecimal} other
     * @returns {boolean}
     */
    equals(other: NexusDecimal): boolean;
}
