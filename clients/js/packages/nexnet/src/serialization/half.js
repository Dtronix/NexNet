/**
 * IEEE 754 binary16 (C# `Half`) conversions. JS has no half type: a half is a `number` holding a value a half can
 * represent exactly.
 * @module
 */

const scratch = new DataView(new ArrayBuffer(8));

/** @type {((x: number) => number) | undefined} */
const nativeRound = /** @type {any} */ (Math).f16round;

/**
 * Rounds half to even: `x` is exact (a scaled double), so the fraction test is exact too.
 * @param {number} x
 * @returns {number}
 */
function roundHalfEven(x) {
  const f = Math.floor(x);
  const d = x - f;
  if (d > 0.5) return f + 1;
  if (d < 0.5) return f;
  return f % 2 === 0 ? f : f + 1;
}

/**
 * The binary16 bits of the half nearest to `value` (ties to even; beyond the half range, ±infinity). NaN keeps its
 * sign and becomes the quiet NaN `0x7E00` (or `0xFE00`).
 * @param {number} value
 * @returns {number} 0–65535.
 */
export function halfToBits(value) {
  scratch.setFloat64(0, value);
  const sign = (scratch.getUint32(0) >>> 16) & 0x8000;
  if (Number.isNaN(value)) return sign | 0x7e00;
  const a = Math.abs(value);
  if (a >= 65520) return sign | 0x7c00; // 65520 is halfway between 65504 (max) and 65536: ties to even go up.
  if (a < 6.103515625e-5) {
    // Subnormal (or zero): a multiple of 2^-24. 1024 rounds up into the smallest normal, 0x0400, which is correct.
    return sign | roundHalfEven(a * 16777216);
  }
  const exponent = ((scratch.getUint32(0) >>> 20) & 0x7ff) - 1023;
  let mantissa = roundHalfEven((a / 2 ** exponent - 1) * 1024);
  let e = exponent + 15;
  if (mantissa === 1024) {
    mantissa = 0;
    e++;
  }
  return e >= 31 ? sign | 0x7c00 : sign | (e << 10) | mantissa;
}

/**
 * The value of binary16 bits. NaN payloads and signs are not kept (a JS NaN has none).
 * @param {number} bits 0–65535.
 * @returns {number}
 */
export function halfFromBits(bits) {
  const sign = bits & 0x8000 ? -1 : 1;
  const e = (bits >>> 10) & 0x1f;
  const m = bits & 0x3ff;
  if (e === 0) return sign * m * 2 ** -24;
  if (e === 31) return m === 0 ? sign * Infinity : NaN;
  return sign * (1 + m / 1024) * 2 ** (e - 15);
}

/**
 * Rounds `value` to the nearest half (`Math.f16round` where the platform has it).
 * @param {number} value
 * @returns {number}
 */
export const roundToHalf = typeof nativeRound === 'function'
  ? nativeRound
  : (/** @type {number} */ value) => halfFromBits(halfToBits(value));
