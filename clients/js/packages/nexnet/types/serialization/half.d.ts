/**
 * IEEE 754 binary16 (C# `Half`) conversions. JS has no half type: a half is a `number` holding a value a half can
 * represent exactly.
 * @module
 */
/**
 * The binary16 bits of the half nearest to `value` (ties to even; beyond the half range, ±infinity). NaN keeps its
 * sign and becomes the quiet NaN `0x7E00` (or `0xFE00`).
 * @param {number} value
 * @returns {number} 0–65535.
 */
export declare function halfToBits(value: number): number;
/**
 * The value of binary16 bits. NaN payloads and signs are not kept (a JS NaN has none).
 * @param {number} bits 0–65535.
 * @returns {number}
 */
export declare function halfFromBits(bits: number): number;
/**
 * Rounds `value` to the nearest half (`Math.f16round` where the platform has it).
 * @param {number} value
 * @returns {number}
 */
export declare const roundToHalf: (x: number) => number;
