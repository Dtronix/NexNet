/**
 * C# `decimal` (protocol specification §10.6).
 * @module
 */

const MAX_MAGNITUDE = (1n << 96n) - 1n;
const MASK32 = 0xffffffffn;
const SCALE_MASK = 0x00ff0000;
const SIGN_BIT = 0x80000000;

/**
 * @param {bigint} magnitude
 * @param {number} scale
 */
function check(magnitude, scale) {
  if (typeof magnitude !== 'bigint' || magnitude < 0n || magnitude > MAX_MAGNITUDE) {
    throw new RangeError(`a decimal magnitude is an integer from 0 to 2^96 - 1; got ${String(magnitude)}`);
  }
  if (!Number.isInteger(scale) || scale < 0 || scale > 28) throw new RangeError(`a decimal scale is 0 to 28; got ${scale}`);
}

/**
 * Divides by 10^`digits`, rounding half to even.
 * @param {bigint} value
 * @param {number} digits
 * @returns {bigint}
 */
function divRound(value, digits) {
  const d = 10n ** BigInt(digits);
  const q = value / d;
  const r = value % d;
  const twice = r * 2n;
  if (twice > d || (twice === d && q % 2n === 1n)) return q + 1n;
  return q;
}

/**
 * C# `decimal`: a 96-bit magnitude, a scale of 0–28 and a sign. Exact; never a float. The scale is kept, so 1.5 and
 * 1.50 are different values on the wire but {@link equals} each other. A negative zero is valid.
 */
export class NexusDecimal {
  /**
   * Unsigned 96-bit magnitude.
   * @readonly
   * @type {bigint}
   */
  magnitude;

  /**
   * Power of ten to divide by, 0–28.
   * @readonly
   * @type {number}
   */
  scale;

  /**
   * @readonly
   * @type {boolean}
   */
  negative;

  /**
   * @param {bigint} magnitude Unsigned 96-bit integer.
   * @param {number} scale Power of ten to divide by, 0–28.
   * @param {boolean} negative
   */
  constructor(magnitude, scale, negative) {
    check(magnitude, scale);
    this.magnitude = magnitude;
    this.scale = scale;
    this.negative = Boolean(negative);
  }

  /**
   * Parses invariant-culture text: an optional sign, digits with an optional `.` fraction, and an optional exponent
   * (`1.5`, `-0.25`, `1e3`). Digits beyond a scale of 28 are rounded half to even, as C# `decimal.Parse` does.
   * @param {string} text
   * @returns {NexusDecimal}
   * @throws {SyntaxError} The text is not a number.
   * @throws {RangeError} The value is beyond the decimal range.
   */
  static parse(text) {
    const m = /^\s*([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?\s*$/.exec(String(text));
    if (!m || (m[2] === '' && (m[3] ?? '') === '')) throw new SyntaxError(`not a decimal number: ${text}`);
    const fraction = m[3] ?? '';
    let magnitude = BigInt((m[2] || '0') + fraction);
    let scale = fraction.length - Number(m[4] ?? 0);
    if (scale < 0) {
      magnitude *= 10n ** BigInt(-scale);
      scale = 0;
    }
    if (scale > 28) {
      magnitude = divRound(magnitude, scale - 28);
      scale = 28;
    }
    // Too many digits for 96 bits: drop fraction digits while there are any (C# rounds to fit).
    while (magnitude > MAX_MAGNITUDE && scale > 0) {
      magnitude = divRound(magnitude, 1);
      scale--;
    }
    if (magnitude > MAX_MAGNITUDE) throw new RangeError(`outside the decimal range: ${text}`);
    return new NexusDecimal(magnitude, scale, m[1] === '-');
  }

  /**
   * The decimal nearest to `value`, from its shortest round-trip text.
   * @param {number} value A finite number.
   * @returns {NexusDecimal}
   */
  static fromNumber(value) {
    if (!Number.isFinite(value)) throw new RangeError(`a decimal is finite; got ${value}`);
    const d = NexusDecimal.parse(String(value));
    return Object.is(value, -0) ? new NexusDecimal(0n, 0, true) : d;
  }

  /**
   * The value of C# `decimal.GetBits`: four signed int32, lo, mid, hi, flags. Throws (`RangeError`) for a scale
   * above 28 or a reserved flag bit.
   * @param {number} lo
   * @param {number} mid
   * @param {number} hi
   * @param {number} flags
   * @returns {NexusDecimal}
   */
  static fromBits(lo, mid, hi, flags) {
    if ((flags & ~(SCALE_MASK | SIGN_BIT)) !== 0) throw new RangeError('decimal flags bits 0-15 and 24-30 must be 0');
    const scale = (flags & SCALE_MASK) >>> 16;
    if (scale > 28) throw new RangeError(`a decimal scale is 0 to 28; got ${scale}`);
    const magnitude = (BigInt(hi >>> 0) << 64n) | (BigInt(mid >>> 0) << 32n) | BigInt(lo >>> 0);
    return new NexusDecimal(magnitude, scale, (flags & SIGN_BIT) !== 0);
  }

  /** C# `decimal.GetBits`: `[lo, mid, hi, flags]`, each a signed int32. @returns {[number, number, number, number]} */
  toBits() {
    const m = this.magnitude;
    return [
      Number(BigInt.asIntN(32, m & MASK32)),
      Number(BigInt.asIntN(32, (m >> 32n) & MASK32)),
      Number(BigInt.asIntN(32, (m >> 64n) & MASK32)),
      (this.scale << 16) | (this.negative ? SIGN_BIT : 0),
    ];
  }

  /** Converts to the nearest double (lossy). A negative zero converts to -0. @returns {number} */
  toNumber() {
    const n = Number(this.toString());
    return this.negative && n === 0 ? -0 : n;
  }

  /**
   * Invariant-culture text, as C# `decimal.ToString(CultureInfo.InvariantCulture)`: the scale's digits are kept
   * (`1.50`), and a negative zero has no sign.
   * @returns {string}
   */
  toString() {
    let digits = this.magnitude.toString();
    if (this.scale > 0) {
      digits = digits.padStart(this.scale + 1, '0');
      digits = `${digits.slice(0, -this.scale)}.${digits.slice(-this.scale)}`;
    }
    return this.negative && this.magnitude !== 0n ? `-${digits}` : digits;
  }

  /**
   * Numeric equality: `1.0` equals `1.00`, and zero equals negative zero (C# `decimal.Equals`).
   * @param {NexusDecimal} other
   * @returns {boolean}
   */
  equals(other) {
    if (!(other instanceof NexusDecimal)) return false;
    if (this.magnitude === 0n && other.magnitude === 0n) return true;
    if (this.negative !== other.negative) return false;
    const s = Math.max(this.scale, other.scale);
    return this.magnitude * 10n ** BigInt(s - this.scale) === other.magnitude * 10n ** BigInt(s - other.scale);
  }
}
