/**
 * Value types for C# primitives that have no exact JS equivalent (01-context §5, D14).
 * Constructors store their fields; the conversions are implemented by J02 in `src/values/`.
 * @module
 */

import { notImplemented } from './internal.js';

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
 * C# `decimal`: a 96-bit magnitude, a scale of 0–28 and a sign. Exact; never a float.
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
    this.magnitude = magnitude;
    this.scale = scale;
    this.negative = negative;
  }

  /** @param {string} text @returns {NexusDecimal} */
  static parse(text) { throw notImplemented('NexusDecimal.parse'); }

  /** Converts to the nearest double (lossy). @returns {number} */
  toNumber() { throw notImplemented('NexusDecimal.toNumber'); }

  /** Invariant-culture text, as C# `decimal.ToString(CultureInfo.InvariantCulture)`. @returns {string} */
  toString() { throw notImplemented('NexusDecimal.toString'); }

  /** Numeric equality (`1.0 == 1.00`). @param {NexusDecimal} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusDecimal.equals'); }
}

/**
 * C# `DateTime`: 100 ns ticks since 0001-01-01 plus the kind.
 */
export class NexusDateTime {
  /**
   * 100 ns ticks since 0001-01-01.
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
    this.ticks = ticks;
    this.kind = kind;
  }

  /** @param {Date} date @param {DateTimeKindValue} [kind] Default `Utc`. @returns {NexusDateTime} */
  static fromDate(date, kind) { throw notImplemented('NexusDateTime.fromDate'); }

  /** Converts to a `Date` (millisecond precision; `Unspecified` is read as UTC). @returns {Date} */
  toDate() { throw notImplemented('NexusDateTime.toDate'); }

  /** Same ticks and kind. @param {NexusDateTime} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusDateTime.equals'); }
}

/**
 * C# `DateTimeOffset`: local clock ticks plus the UTC offset in minutes.
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
   * @param {bigint} ticks Clock ticks in the offset's time zone.
   * @param {number} offsetMinutes
   */
  constructor(ticks, offsetMinutes) {
    this.ticks = ticks;
    this.offsetMinutes = offsetMinutes;
  }

  /** @param {Date} date @param {number} [offsetMinutes] Default 0. @returns {NexusDateTimeOffset} */
  static fromDate(date, offsetMinutes) { throw notImplemented('NexusDateTimeOffset.fromDate'); }

  /** @returns {Date} */
  toDate() { throw notImplemented('NexusDateTimeOffset.toDate'); }

  /** @param {NexusDateTimeOffset} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusDateTimeOffset.equals'); }
}

/**
 * C# `TimeSpan`: signed 100 ns ticks.
 */
export class NexusTimeSpan {
  /**
   * @readonly
   * @type {bigint}
   */
  ticks;

  /** @param {bigint} ticks */
  constructor(ticks) {
    this.ticks = ticks;
  }

  /** @param {number} milliseconds @returns {NexusTimeSpan} */
  static fromMilliseconds(milliseconds) { throw notImplemented('NexusTimeSpan.fromMilliseconds'); }

  /** @returns {number} */
  get totalMilliseconds() { throw notImplemented('NexusTimeSpan.totalMilliseconds'); }

  /** @param {NexusTimeSpan} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusTimeSpan.equals'); }
}

/**
 * C# `DateOnly`: day number since 0001-01-01.
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
    this.dayNumber = dayNumber;
  }

  /** @param {number} year @param {number} month 1–12 @param {number} day @returns {NexusDateOnly} */
  static fromParts(year, month, day) { throw notImplemented('NexusDateOnly.fromParts'); }

  /** @param {NexusDateOnly} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusDateOnly.equals'); }
}

/**
 * C# `TimeOnly`: 100 ns ticks since midnight.
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
    this.ticks = ticks;
  }

  /** @param {NexusTimeOnly} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusTimeOnly.equals'); }
}

/**
 * C# `System.Version`. `build` and `revision` are -1 when absent, as in C#.
 * Exported as both `NexusVersion` and `Version` (the name the G03 type mapper emits).
 */
export class NexusVersion {
  /**
   * @readonly
   * @type {number}
   */
  major;

  /**
   * @readonly
   * @type {number}
   */
  minor;

  /**
   * -1 when absent.
   * @readonly
   * @type {number}
   */
  build;

  /**
   * -1 when absent.
   * @readonly
   * @type {number}
   */
  revision;

  /**
   * @param {number} major
   * @param {number} minor
   * @param {number} [build]
   * @param {number} [revision]
   */
  constructor(major, minor, build = -1, revision = -1) {
    this.major = major;
    this.minor = minor;
    this.build = build;
    this.revision = revision;
  }

  /** @param {string} text @returns {NexusVersion} */
  static parse(text) { throw notImplemented('NexusVersion.parse'); }

  /** @returns {string} */
  toString() { throw notImplemented('NexusVersion.toString'); }

  /** @param {NexusVersion} other @returns {boolean} */
  equals(other) { throw notImplemented('NexusVersion.equals'); }
}

/**
 * Builds a tuple value. C# `ValueTuple`/`Tuple` map to JS arrays; this helper only fixes the TS tuple type.
 *
 * @template {unknown[]} T
 * @param {T} items
 * @returns {T}
 */
export function tuple(...items) {
  return items;
}
