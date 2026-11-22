/**
 * Date and time values: C# `DateTime`, `DateTimeOffset`, `TimeSpan`, `DateOnly` and `TimeOnly` (protocol
 * specification §10.6). Ticks are 100 ns `bigint`s, so every value round-trips exactly.
 * @module
 */
/** Ticks of 1970-01-01T00:00:00Z. */
export declare const UNIX_EPOCH_TICKS = 621355968000000000n;
/** Largest tick count of a date and time: 9999-12-31T23:59:59.9999999. */
export declare const MAX_TICKS = 3155378975999999999n;
/** Largest day number: 9999-12-31. */
export declare const MAX_DAY_NUMBER = 3652058;
/** Largest `TimeOnly` tick count: 23:59:59.9999999. */
export declare const MAX_TIME_TICKS = 863999999999n;
/**
 * `DateTime.Kind`. Mirrors C# `System.DateTimeKind`.
 */
export declare const DateTimeKind: Readonly<{
    Unspecified: 0;
    Utc: 1;
    Local: 2;
}>;
export type DateTimeKindValue = typeof DateTimeKind[keyof typeof DateTimeKind];
/** @typedef {typeof DateTimeKind[keyof typeof DateTimeKind]} DateTimeKindValue */
/**
 * C# `DateTime`: 100 ns ticks since 0001-01-01 plus the kind.
 *
 * `Unspecified` and `Utc` values hold the ticks of the value, 0 to {@link MAX_TICKS}, and cross the wire unchanged.
 * A `Local` value names an instant: it holds that instant's **UTC** ticks (the wire value, `DateTime.ToBinary`), so
 * it round-trips exactly whatever the time zone. C# shows it in the reader's local zone; {@link toDate} gives the
 * same instant. A local time just after 0001-01-01 in a zone east of UTC has UTC ticks below 0, down to minus one day.
 */
export declare class NexusDateTime {
    /**
     * 100 ns ticks since 0001-01-01 (UTC ticks for `Local`).
     * @readonly
     * @type {bigint}
     */
    readonly ticks: bigint;
    /**
     * @readonly
     * @type {DateTimeKindValue}
     */
    readonly kind: DateTimeKindValue;
    /**
     * @param {bigint} ticks
     * @param {DateTimeKindValue} [kind]
     */
    constructor(ticks: bigint, kind?: DateTimeKindValue);
    /**
     * @param {Date} date
     * @param {DateTimeKindValue} [kind] Default `Utc`. `Unspecified` takes the UTC clock of `date`.
     * @returns {NexusDateTime}
     */
    static fromDate(date: Date, kind?: DateTimeKindValue): NexusDateTime;
    /**
     * The value of C# `DateTime.ToBinary` (§10.6): bits 0–61 the ticks, bits 62–63 the kind. Throws (`RangeError`)
     * for Unspecified or Utc ticks above {@link MAX_TICKS}.
     * @param {bigint} value A signed 64-bit integer.
     * @returns {NexusDateTime}
     */
    static fromBinary(value: bigint): NexusDateTime;
    /** C# `DateTime.ToBinary`: the signed 64-bit wire value. @returns {bigint} */
    toBinary(): bigint;
    /** Converts to a `Date` (millisecond precision; `Unspecified` is read as UTC). @returns {Date} */
    toDate(): Date;
    /** Same ticks and kind. @param {NexusDateTime} other @returns {boolean} */
    equals(other: NexusDateTime): boolean;
}
/**
 * C# `DateTimeOffset`: local clock ticks plus the UTC offset in whole minutes (-840 to 840). The wire carries the
 * UTC ticks and the offset.
 */
export declare class NexusDateTimeOffset {
    /**
     * Clock ticks in the offset's time zone.
     * @readonly
     * @type {bigint}
     */
    readonly ticks: bigint;
    /**
     * @readonly
     * @type {number}
     */
    readonly offsetMinutes: number;
    /**
     * @param {bigint} ticks Clock ticks in the offset's time zone, 0 to {@link MAX_TICKS}.
     * @param {number} offsetMinutes -840 to 840. The UTC ticks must be within 0 to {@link MAX_TICKS} too.
     */
    constructor(ticks: bigint, offsetMinutes: number);
    /**
     * @param {bigint} utcTicks
     * @param {number} offsetMinutes
     * @returns {NexusDateTimeOffset}
     */
    static fromUtcTicks(utcTicks: bigint, offsetMinutes: number): NexusDateTimeOffset;
    /** @param {Date} date @param {number} [offsetMinutes] Default 0. @returns {NexusDateTimeOffset} */
    static fromDate(date: Date, offsetMinutes?: number): NexusDateTimeOffset;
    /** The UTC ticks of the instant (the wire value). @returns {bigint} */
    get utcTicks(): bigint;
    /** The instant, truncated to milliseconds. @returns {Date} */
    toDate(): Date;
    /** Same instant, whatever the offsets (C# `DateTimeOffset.Equals`). @param {NexusDateTimeOffset} other @returns {boolean} */
    equals(other: NexusDateTimeOffset): boolean;
}
/**
 * C# `TimeSpan`: signed 100 ns ticks, the full int64 range.
 */
export declare class NexusTimeSpan {
    /**
     * @readonly
     * @type {bigint}
     */
    readonly ticks: bigint;
    /** @param {bigint} ticks */
    constructor(ticks: bigint);
    /** Rounds to the nearest tick. @param {number} milliseconds @returns {NexusTimeSpan} */
    static fromMilliseconds(milliseconds: number): NexusTimeSpan;
    /** @returns {number} */
    get totalMilliseconds(): number;
    /** @param {NexusTimeSpan} other @returns {boolean} */
    equals(other: NexusTimeSpan): boolean;
}
/**
 * C# `DateOnly`: the day number since 0001-01-01, 0 to {@link MAX_DAY_NUMBER} (9999-12-31).
 */
export declare class NexusDateOnly {
    #private;
    /**
     * Days since 0001-01-01.
     * @readonly
     * @type {number}
     */
    readonly dayNumber: number;
    /** @param {number} dayNumber */
    constructor(dayNumber: number);
    /** @param {number} year 1–9999 @param {number} month 1–12 @param {number} day @returns {NexusDateOnly} */
    static fromParts(year: number, month: number, day: number): NexusDateOnly;
    /** @returns {number} */
    get year(): number;
    /** 1–12. @returns {number} */
    get month(): number;
    /** @returns {number} */
    get day(): number;
    /** @param {NexusDateOnly} other @returns {boolean} */
    equals(other: NexusDateOnly): boolean;
}
/**
 * C# `TimeOnly`: 100 ns ticks since midnight, 0 to {@link MAX_TIME_TICKS}.
 */
export declare class NexusTimeOnly {
    /**
     * 100 ns ticks since midnight.
     * @readonly
     * @type {bigint}
     */
    readonly ticks: bigint;
    /** @param {bigint} ticks */
    constructor(ticks: bigint);
    /**
     * @param {number} hour 0–23
     * @param {number} minute 0–59
     * @param {number} [second] 0–59
     * @param {number} [millisecond] 0–999
     * @returns {NexusTimeOnly}
     */
    static fromParts(hour: number, minute: number, second?: number, millisecond?: number): NexusTimeOnly;
    /** @param {NexusTimeOnly} other @returns {boolean} */
    equals(other: NexusTimeOnly): boolean;
}
