/**
 * Date and time values: C# `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly` and `TimeOnly` (protocol
 * specification §10.6). Ticks are 100 ns `bigint`s, so every value round-trips exactly.
 * @module
 */

/** Ticks per millisecond. */
const TICKS_PER_MS = 10_000n;

/** Ticks per minute. */
const TICKS_PER_MINUTE = 600_000_000n;

/** Ticks per day. */
const TICKS_PER_DAY = 864_000_000_000n;

/** Ticks of 1970-01-01T00:00:00Z. */
export const UNIX_EPOCH_TICKS = 621_355_968_000_000_000n;

/** Largest tick count of a date and time: 9999-12-31T23:59:59.9999999. */
export const MAX_TICKS = 3_155_378_975_999_999_999n;

/** Largest day number: 9999-12-31. */
export const MAX_DAY_NUMBER = 3_652_058;

/** Largest `TimeOnly` tick count: 23:59:59.9999999. */
export const MAX_TIME_TICKS = 863_999_999_999n;

/** Milliseconds of 0001-01-01T00:00:00Z since the Unix epoch. */
const DAY0_MS = -62_135_596_800_000;

const MS_PER_DAY = 86_400_000;

const BIT62 = 1n << 62n;
const TICKS_MASK = BIT62 - 1n;

/**
 * Floor division of bigints.
 * @param {bigint} a
 * @param {bigint} b
 * @returns {bigint}
 */
function floorDiv(a, b) {
  const q = a / b;
  return (a % b !== 0n && (a < 0n) !== (b < 0n)) ? q - 1n : q;
}

/**
 * @param {Date} date
 * @returns {bigint} UTC ticks of `date`.
 */
function ticksOfDate(date) {
  if (!(date instanceof Date)) throw new TypeError('expected a Date');
  const ms = date.getTime();
  if (Number.isNaN(ms)) throw new RangeError('invalid Date');
  return BigInt(ms) * TICKS_PER_MS + UNIX_EPOCH_TICKS;
}

/**
 * @param {bigint} ticks UTC ticks.
 * @returns {Date} The instant, truncated to milliseconds.
 */
function dateOfTicks(ticks) {
  return new Date(Number(floorDiv(ticks - UNIX_EPOCH_TICKS, TICKS_PER_MS)));
}

/**
 * @param {unknown} ticks
 * @param {string} what
 * @param {bigint} min
 * @param {bigint} max
 */
function checkTicks(ticks, what, min, max) {
  if (typeof ticks !== 'bigint') throw new TypeError(`${what} ticks must be a bigint`);
  if (ticks < min || ticks > max) throw new RangeError(`${what} ticks must be ${min} to ${max}; got ${ticks}`);
}

/**
 * `DateTime.Kind`. Mirrors C# `System.DateTimeKind`.
 */
export const DateTimeKind = Object.freeze({
  Unspecified: 0,
  Utc: 1,
  Local: 2,
});

/** @typedef {typeof DateTimeKind[keyof typeof DateTimeKind]} DateTimeKindValue */

/**
 * C# `DateTime`: 100 ns ticks since 0001-01-01 plus the kind.
 *
 * `Unspecified` and `Utc` values hold the ticks of the value, 0 to {@link MAX_TICKS}, and cross the wire unchanged.
 * A `Local` value names an instant: it holds that instant's **UTC** ticks (the wire value, `DateTime.ToBinary`), so
 * it round-trips exactly whatever the time zone. C# shows it in the reader's local zone; {@link toDate} gives the
 * same instant. A local time just after 0001-01-01 in a zone east of UTC has UTC ticks below 0, down to minus one day.
 */
export class NexusDateTime {
  /**
   * 100 ns ticks since 0001-01-01 (UTC ticks for `Local`).
   * @readonly
   * @type {bigint}
   */
  ticks;

  /**
   * @readonly
   * @type {DateTimeKindValue}
   */
  kind;

  /**
   * @param {bigint} ticks
   * @param {DateTimeKindValue} [kind]
   */
  constructor(ticks, kind = DateTimeKind.Unspecified) {
    if (kind !== DateTimeKind.Unspecified && kind !== DateTimeKind.Utc && kind !== DateTimeKind.Local) {
      throw new RangeError(`unknown DateTimeKind ${String(kind)}`);
    }
    if (kind === DateTimeKind.Local) checkTicks(ticks, 'DateTime', 1n - TICKS_PER_DAY, BIT62 - TICKS_PER_DAY);
    else checkTicks(ticks, 'DateTime', 0n, MAX_TICKS);
    this.ticks = ticks;
    this.kind = kind;
  }

  /**
   * @param {Date} date
   * @param {DateTimeKindValue} [kind] Default `Utc`. `Unspecified` takes the UTC clock of `date`.
   * @returns {NexusDateTime}
   */
  static fromDate(date, kind = DateTimeKind.Utc) {
    return new NexusDateTime(ticksOfDate(date), kind);
  }

  /**
   * The value of C# `DateTime.ToBinary` (§10.6): bits 0–61 the ticks, bits 62–63 the kind. Throws (`RangeError`)
   * for Unspecified or Utc ticks above {@link MAX_TICKS}.
   * @param {bigint} value A signed 64-bit integer.
   * @returns {NexusDateTime}
   */
  static fromBinary(value) {
    const bits = BigInt.asUintN(64, value);
    let ticks = bits & TICKS_MASK;
    if (bits >> 63n) {
      if (ticks > BIT62 - TICKS_PER_DAY) ticks -= BIT62;
      return new NexusDateTime(ticks, DateTimeKind.Local);
    }
    return new NexusDateTime(ticks, bits >> 62n ? DateTimeKind.Utc : DateTimeKind.Unspecified);
  }

  /** C# `DateTime.ToBinary`: the signed 64-bit wire value. @returns {bigint} */
  toBinary() {
    switch (this.kind) {
      case DateTimeKind.Utc: return this.ticks | BIT62;
      case DateTimeKind.Local: return BigInt.asIntN(64, (this.ticks < 0n ? this.ticks + BIT62 : this.ticks) | (1n << 63n));
      default: return this.ticks;
    }
  }

  /** Converts to a `Date` (millisecond precision; `Unspecified` is read as UTC). @returns {Date} */
  toDate() {
    return dateOfTicks(this.ticks);
  }

  /** Same ticks and kind. @param {NexusDateTime} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusDateTime && other.ticks === this.ticks && other.kind === this.kind;
  }
}

/**
 * C# `DateTimeOffset`: local clock ticks plus the UTC offset in whole minutes (-840 to 840). The wire carries the
 * UTC ticks and the offset.
 */
export class NexusDateTimeOffset {
  /**
   * Clock ticks in the offset's time zone.
   * @readonly
   * @type {bigint}
   */
  ticks;

  /**
   * @readonly
   * @type {number}
   */
  offsetMinutes;

  /**
   * @param {bigint} ticks Clock ticks in the offset's time zone, 0 to {@link MAX_TICKS}.
   * @param {number} offsetMinutes -840 to 840. The UTC ticks must be within 0 to {@link MAX_TICKS} too.
   */
  constructor(ticks, offsetMinutes) {
    checkTicks(ticks, 'DateTimeOffset', 0n, MAX_TICKS);
    if (!Number.isInteger(offsetMinutes) || offsetMinutes < -840 || offsetMinutes > 840) {
      throw new RangeError(`a DateTimeOffset offset is -840 to 840 minutes; got ${offsetMinutes}`);
    }
    const utc = ticks - BigInt(offsetMinutes) * TICKS_PER_MINUTE;
    if (utc < 0n || utc > MAX_TICKS) throw new RangeError('the UTC ticks of a DateTimeOffset must be 0 to MAX_TICKS');
    this.ticks = ticks;
    this.offsetMinutes = offsetMinutes;
  }

  /**
   * @param {bigint} utcTicks
   * @param {number} offsetMinutes
   * @returns {NexusDateTimeOffset}
   */
  static fromUtcTicks(utcTicks, offsetMinutes) {
    if (typeof utcTicks !== 'bigint') throw new TypeError('DateTimeOffset ticks must be a bigint');
    if (!Number.isInteger(offsetMinutes)) throw new RangeError(`a DateTimeOffset offset is whole minutes; got ${offsetMinutes}`);
    return new NexusDateTimeOffset(utcTicks + BigInt(offsetMinutes) * TICKS_PER_MINUTE, offsetMinutes);
  }

  /** @param {Date} date @param {number} [offsetMinutes] Default 0. @returns {NexusDateTimeOffset} */
  static fromDate(date, offsetMinutes = 0) {
    return NexusDateTimeOffset.fromUtcTicks(ticksOfDate(date), offsetMinutes);
  }

  /** The UTC ticks of the instant (the wire value). @returns {bigint} */
  get utcTicks() {
    return this.ticks - BigInt(this.offsetMinutes) * TICKS_PER_MINUTE;
  }

  /** The instant, truncated to milliseconds. @returns {Date} */
  toDate() {
    return dateOfTicks(this.utcTicks);
  }

  /** Same instant, whatever the offsets (C# `DateTimeOffset.Equals`). @param {NexusDateTimeOffset} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusDateTimeOffset && other.utcTicks === this.utcTicks;
  }
}

/**
 * C# `TimeSpan`: signed 100 ns ticks, the full int64 range.
 */
export class NexusTimeSpan {
  /**
   * @readonly
   * @type {bigint}
   */
  ticks;

  /** @param {bigint} ticks */
  constructor(ticks) {
    checkTicks(ticks, 'TimeSpan', -(1n << 63n), (1n << 63n) - 1n);
    this.ticks = ticks;
  }

  /** Rounds to the nearest tick. @param {number} milliseconds @returns {NexusTimeSpan} */
  static fromMilliseconds(milliseconds) {
    if (!Number.isFinite(milliseconds)) throw new RangeError(`a TimeSpan is finite; got ${milliseconds}`);
    return new NexusTimeSpan(BigInt(Math.round(milliseconds * 10_000)));
  }

  /** @returns {number} */
  get totalMilliseconds() {
    return Number(this.ticks) / 10_000;
  }

  /** @param {NexusTimeSpan} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusTimeSpan && other.ticks === this.ticks;
  }
}

/**
 * C# `DateOnly`: the day number since 0001-01-01, 0 to {@link MAX_DAY_NUMBER} (9999-12-31).
 */
export class NexusDateOnly {
  /**
   * Days since 0001-01-01.
   * @readonly
   * @type {number}
   */
  dayNumber;

  /** @param {number} dayNumber */
  constructor(dayNumber) {
    if (!Number.isInteger(dayNumber) || dayNumber < 0 || dayNumber > MAX_DAY_NUMBER) {
      throw new RangeError(`a DateOnly day number is 0 to ${MAX_DAY_NUMBER}; got ${dayNumber}`);
    }
    this.dayNumber = dayNumber;
  }

  /** @param {number} year 1–9999 @param {number} month 1–12 @param {number} day @returns {NexusDateOnly} */
  static fromParts(year, month, day) {
    const d = new Date(0);
    d.setUTCFullYear(year, month - 1, day);
    if (d.getUTCFullYear() !== year || d.getUTCMonth() !== month - 1 || d.getUTCDate() !== day) {
      throw new RangeError(`not a date: ${year}-${month}-${day}`);
    }
    return new NexusDateOnly((d.getTime() - DAY0_MS) / MS_PER_DAY);
  }

  /** @returns {Date} */
  #date() {
    return new Date(DAY0_MS + this.dayNumber * MS_PER_DAY);
  }

  /** @returns {number} */
  get year() {
    return this.#date().getUTCFullYear();
  }

  /** 1–12. @returns {number} */
  get month() {
    return this.#date().getUTCMonth() + 1;
  }

  /** @returns {number} */
  get day() {
    return this.#date().getUTCDate();
  }

  /** @param {NexusDateOnly} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusDateOnly && other.dayNumber === this.dayNumber;
  }
}

/**
 * C# `TimeOnly`: 100 ns ticks since midnight, 0 to {@link MAX_TIME_TICKS}.
 */
export class NexusTimeOnly {
  /**
   * 100 ns ticks since midnight.
   * @readonly
   * @type {bigint}
   */
  ticks;

  /** @param {bigint} ticks */
  constructor(ticks) {
    checkTicks(ticks, 'TimeOnly', 0n, MAX_TIME_TICKS);
    this.ticks = ticks;
  }

  /**
   * @param {number} hour 0–23
   * @param {number} minute 0–59
   * @param {number} [second] 0–59
   * @param {number} [millisecond] 0–999
   * @returns {NexusTimeOnly}
   */
  static fromParts(hour, minute, second = 0, millisecond = 0) {
    const parts = [[hour, 23], [minute, 59], [second, 59], [millisecond, 999]];
    for (const [v, max] of parts) {
      if (!Number.isInteger(v) || v < 0 || v > max) throw new RangeError(`not a time of day: ${hour}:${minute}:${second}.${millisecond}`);
    }
    return new NexusTimeOnly(BigInt(((hour * 60 + minute) * 60 + second) * 1000 + millisecond) * TICKS_PER_MS);
  }

  /** @param {NexusTimeOnly} other @returns {boolean} */
  equals(other) {
    return other instanceof NexusTimeOnly && other.ticks === this.ticks;
  }
}
